using System.Globalization;
using System.Text.Json;

namespace MeshCore.Windows.Core;

public sealed class ElevationService
{
    private readonly HttpClient _client;
    public ElevationService(HttpClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }

    public async Task<double[]> QueryAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(request);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 100) throw new ArgumentException("Expected 1–100 coordinates.");
        var latitude = new List<string>(); var longitude = new List<string>();
        foreach (var point in root.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2 ||
                !point[0].TryGetDouble(out double lat) || !point[1].TryGetDouble(out double lon) ||
                !double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
                throw new ArgumentException("Invalid coordinate.");
            latitude.Add(lat.ToString("F6", CultureInfo.InvariantCulture)); longitude.Add(lon.ToString("F6", CultureInfo.InvariantCulture));
        }
        string url = "https://api.open-meteo.com/v1/elevation?latitude=" + string.Join(",", latitude) + "&longitude=" + string.Join(",", longitude);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("Elevation response is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[4096]; int n;
        while ((n = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + n > 65536) throw new InvalidDataException("Elevation response is too large.");
            output.Write(buffer, 0, n);
        }
        using var data = JsonDocument.Parse(output.ToArray());
        if (!data.RootElement.TryGetProperty("elevation", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != latitude.Count)
            throw new InvalidDataException("Elevation data is incomplete.");
        var result = new double[latitude.Count]; int i = 0;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double value) || !double.IsFinite(value)) throw new InvalidDataException("Invalid elevation data.");
            result[i++] = value;
        }
        return result;
    }
}
