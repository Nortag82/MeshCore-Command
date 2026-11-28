using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public static class Maidenhead
{
    public static string Normalize(string? locator)
    {
        string grid = locator?.Trim().ToUpperInvariant() ?? "";
        return Regex.IsMatch(grid, @"\A[A-R]{2}[0-9]{2}(?:[A-X]{2}(?:[0-9]{2})?)?\z") ? grid : "";
    }

    public static (double Latitude, double Longitude)? Center(string? locator)
    {
        string grid = Normalize(locator);
        if (grid.Length == 0) return null;
        double longitude = -180 + (grid[0] - 'A') * 20 + (grid[2] - '0') * 2;
        double latitude = -90 + (grid[1] - 'A') * 10 + (grid[3] - '0');
        double width = 2, height = 1;
        if (grid.Length >= 6)
        {
            width /= 24;
            height /= 24;
            longitude += (grid[4] - 'A') * width;
            latitude += (grid[5] - 'A') * height;
        }
        if (grid.Length == 8)
        {
            width /= 10;
            height /= 10;
            longitude += (grid[6] - '0') * width;
            latitude += (grid[7] - '0') * height;
        }
        return (latitude + height / 2, longitude + width / 2);
    }
}
