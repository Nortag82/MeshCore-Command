using System.Globalization;

namespace MeshCore.Windows.Core;

public sealed record GpsPosition(double Latitude, double Longitude);

/// <summary>Parses checksum-verified RMC and GGA fixes from a GPS receiver.</summary>
public static class NmeaPosition
{
    public static GpsPosition? Parse(string sentence)
    {
        if (sentence is null) return null;
        var text = sentence.Trim();
        int star = text.LastIndexOf('*');
        if (!text.StartsWith('$') || star < 1 || star != text.Length - 3 || !byte.TryParse(text.AsSpan(star + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte expected)) return null;
        byte checksum = 0;
        for (int i = 1; i < star; i++)
        {
            if (text[i] > 127) return null;
            checksum ^= (byte)text[i];
        }
        if (checksum != expected) return null;
        string[] fields = text[1..star].Split(',');
        if (fields.Length < 7 || fields[0].Length < 3) return null;
        string kind = fields[0][^3..];
        string latitude, ns, longitude, ew;
        if (kind == "RMC" && fields[2] == "A") (latitude, ns, longitude, ew) = (fields[3], fields[4], fields[5], fields[6]);
        else if (kind == "GGA" && int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int quality) && quality > 0)
            (latitude, ns, longitude, ew) = (fields[2], fields[3], fields[4], fields[5]);
        else return null;
        if (ns is not ("N" or "S") || ew is not ("E" or "W")) return null;
        if (!Coordinate(latitude, 2, out double lat) || !Coordinate(longitude, 3, out double lon)) return null;
        if (ns == "S") lat = -lat;
        if (ew == "W") lon = -lon;
        return Math.Abs(lat) <= 85 && Math.Abs(lon) <= 180 ? new GpsPosition(lat, lon) : null;
    }

    private static bool Coordinate(string text, int width, out double result)
    {
        result = 0;
        if (text.Length <= width || !int.TryParse(text.AsSpan(0, width), NumberStyles.None, CultureInfo.InvariantCulture, out int degrees) ||
            !double.TryParse(text.AsSpan(width), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes) ||
            !double.IsFinite(minutes) || minutes < 0 || minutes >= 60) return false;
        result = degrees + minutes / 60;
        return true;
    }
}
