using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MeshCore.Windows.Core;

public sealed record OfflineMapInfo(bool Available, string Name, string Format, int? MaxZoom);
public sealed record MapTile(byte[] Data, string ContentType);

/// <summary>Read-only raster MBTiles and range-limited PMTiles access.</summary>
public sealed class OfflineMapStore
{
    private string? _path;
    public OfflineMapInfo Info
    {
        get
        {
            string? path = Volatile.Read(ref _path);
            bool available = path is not null && File.Exists(path);
            int? maxZoom = null;
            if (available && path is not null && Path.GetExtension(path).Equals(".pmtiles", StringComparison.OrdinalIgnoreCase))
            {
                using var file = File.OpenRead(path!);
                file.Seek(101, SeekOrigin.Begin); int value = file.ReadByte();
                if (value is >= 0 and <= 22) maxZoom = value;
            }
            return new OfflineMapInfo(available, path is null ? "" : Path.GetFileName(path), path is null ? "" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), maxZoom);
        }
    }

    public void Select(string path)
    {
        path = Path.GetFullPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path) || extension is not (".mbtiles" or ".pmtiles")) throw new InvalidDataException("Choose an MBTiles or PMTiles file.");
        if (extension == ".pmtiles")
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (file.Read(header) != 8 || !header.SequenceEqual("PMTiles\x03"u8)) throw new InvalidDataException("Invalid PMTiles map.");
        }
        else
        {
            using var connection = OpenDatabase(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT tile_data FROM tiles WHERE length(tile_data) <= 2000000 LIMIT 1";
            if (command.ExecuteScalar() is not byte[] data || ContentType(data) is null) throw new InvalidDataException("Use raster PNG, JPEG or WebP MBTiles.");
        }
        Volatile.Write(ref _path, path);
    }

    public MapTile? ReadTile(int zoom, long x, long y)
    {
        var path = Volatile.Read(ref _path);
        if (path is null || !Path.GetExtension(path).Equals(".mbtiles", StringComparison.OrdinalIgnoreCase) || zoom is < 0 or > 22 || x < 0 || y < 0 || x >= 1L << zoom || y >= 1L << zoom) return null;
        using var connection = OpenDatabase(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y AND length(tile_data) <= 2000000 LIMIT 1";
        command.Parameters.AddWithValue("$z", zoom); command.Parameters.AddWithValue("$x", x); command.Parameters.AddWithValue("$y", (1L << zoom) - 1 - y);
        if (command.ExecuteScalar() is not byte[] data || data.Length > 2_000_000) return null;
        string? type = ContentType(data);
        return type is null ? null : new MapTile(data, type);
    }

    public (byte[] Data, long Size, long End)? ReadRange(long start, long? end)
    {
        var path = Volatile.Read(ref _path);
        if (path is null || !Path.GetExtension(path).Equals(".pmtiles", StringComparison.OrdinalIgnoreCase)) return null;
        using var file = File.OpenRead(path);
        long size = file.Length, last = Math.Min(end ?? size - 1, size - 1);
        if (start < 0 || start >= size || last < start || last - start > 8_000_000) return null;
        byte[] data = new byte[checked((int)(last - start + 1))];
        file.Seek(start, SeekOrigin.Begin); file.ReadExactly(data);
        return (data, size, last);
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static string? ContentType(byte[] data)
    {
        if (data.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
