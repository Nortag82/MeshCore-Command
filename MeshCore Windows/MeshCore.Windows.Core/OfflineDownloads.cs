using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record OfflineDownloadStatus(string Status, string Message, string? Name = null);

/// <summary>Downloads a bounded area with the same verified Protomaps helper as the existing app.</summary>
public sealed class OfflineDownloads : IAsyncDisposable
{
    private const string HelperUrl = "https://github.com/protomaps/go-pmtiles/releases/download/v1.31.2/go-pmtiles_1.31.2_Windows_x86_64.zip";
    private const string HelperHash = "a658baa4d7e55020aef6ca17bd9ff9faa1582671266b36f58c52db0ac8e785a1";
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly OfflineMapStore _maps;
    private readonly Action<string> _saveSelection;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _work = Task.CompletedTask;
    private bool _disposed;
    private OfflineDownloadStatus _state = new("idle", "Select an area on the map.");

    public OfflineDownloads(HttpClient http, string directory, OfflineMapStore maps, Action<string> saveSelection)
    { _http = http; _directory = Path.GetFullPath(directory); _maps = maps; _saveSelection = saveSelection; }
    public OfflineDownloadStatus Status { get { lock (_gate) return _state; } }
    private void Set(string state, string message, string? name = null) { lock (_gate) _state = new(state, message, name); }

    public void Start(double[] bounds, int zoom)
    {
        if (bounds is null || bounds.Length != 4 || zoom is < 8 or > 15 || bounds.Any(x => !double.IsFinite(x))) throw new ArgumentException("Invalid bounds or detail level.");
        double west = bounds[0], south = bounds[1], east = bounds[2], north = bounds[3];
        if (west < -180 || west >= east || east > 180 || south < -85 || south >= north || north > 85 || east - west > 10 || north - south > 10)
            throw new ArgumentException("Choose a smaller area, at most 10 degrees wide and high.");
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OfflineDownloads));
            if (_state.Status == "running") throw new InvalidOperationException("A map download is already running.");
            _state = new("running", "Preparing offline map download…");
            _work = Task.Run(() => DownloadAsync((double[])bounds.Clone(), zoom));
        }
    }

    private async Task DownloadAsync(double[] bounds, int zoom)
    {
        string? helper = null, temporaryMap = null;
        try
        {
            Directory.CreateDirectory(_directory);
            Set("running", "Downloading the verified map download tool…");
            byte[] archive = await ReadLimitedAsync(HelperUrl, 100_000_000, TimeSpan.FromMinutes(2));
            if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(HelperHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Map download tool failed its integrity check.");
            using (var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(item => item.FullName == "pmtiles.exe").ToArray();
                if (entries.Length != 1 || entries[0].Length is < 1 or > 100_000_000) throw new InvalidDataException("Invalid map download tool.");
                helper = Path.Combine(_directory, ".helper-" + Guid.NewGuid().ToString("N") + ".exe");
                await using var output = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var input = entries[0].Open();
                await input.CopyToAsync(output);
                if (output.Length != entries[0].Length) throw new InvalidDataException("Incomplete map download tool.");
            }
            Set("running", "Finding a current Protomaps map…");
            byte[] metadata = await ReadLimitedAsync("https://build-metadata.protomaps.dev/builds.json", 2_000_000, TimeSpan.FromSeconds(30));
            using var json = JsonDocument.Parse(metadata);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Map metadata is invalid.");
            var keys = new List<string>();
            foreach (var item in json.RootElement.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
                    Regex.IsMatch(key.GetString()!, "^20[0-9]{6}\\.pmtiles$") && version.GetString()!.StartsWith("4.", StringComparison.Ordinal)) keys.Add(key.GetString()!);
            string selected = keys.OrderByDescending(x => x, StringComparer.Ordinal).FirstOrDefault() ?? throw new InvalidDataException("No compatible Protomaps map is available.");
            Set("running", "Downloading map area. Larger areas and more detail take longer…");
            temporaryMap = Path.Combine(_directory, ".map-" + Guid.NewGuid().ToString("N") + ".pmtiles");
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("extract"); start.ArgumentList.Add("https://build.protomaps.com/" + selected); start.ArgumentList.Add(temporaryMap);
            start.ArgumentList.Add("--bbox=" + string.Join(",", bounds.Select(x => x.ToString("F6", CultureInfo.InvariantCulture))));
            start.ArgumentList.Add("--maxzoom=" + zoom.ToString(CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new IOException("Could not start the map download tool.");
            Task<string> stderr = process.StandardError.ReadToEndAsync(); Task stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromMinutes(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw new TimeoutException("Map download timed out."); }
            string error = await stderr; await stdout;
            if (process.ExitCode != 0) throw new IOException("Map extraction failed: " + error[^Math.Min(error.Length, 400)..]);
            // Verify the generated format before exposing the new file.
            var verifier = new OfflineMapStore(); verifier.Select(temporaryMap);
            string target = Path.Combine(_directory, "map-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".pmtiles");
            File.Move(temporaryMap, target); temporaryMap = null;
            _saveSelection(target); _maps.Select(target);
            Set("complete", "Download complete: " + Path.GetFileName(target), Path.GetFileName(target));
        }
        catch (Exception error) { Set("error", "Download failed: " + error.Message); }
        finally
        {
            if (temporaryMap is not null) try { File.Delete(temporaryMap); } catch (IOException) { }
            if (helper is not null) try { File.Delete(helper); } catch (IOException) { }
        }
    }

    private async Task<byte[]> ReadLimitedAsync(string url, int limit, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.StartsWith("https://build-metadata.protomaps.dev/", StringComparison.Ordinal)) request.Headers.TryAddWithoutValidation("Origin", "https://maps.protomaps.com");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Download exceeds the size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token)) != 0)
        { if (output.Length + read > limit) throw new InvalidDataException("Download exceeds the size limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        Task work;
        lock (_gate) { if (_disposed) return; _disposed = true; _stop.Cancel(); work = _work; }
        await work;
        _stop.Dispose();
    }
}
