using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeshCore.Windows.Core;

/// <summary>Checks releases and verifies installers; never launches an installer.</summary>
public sealed class UpdaterClient
{
    private static readonly Uri Releases = new("https://api.github.com/repos/Nortag82/MeshCore-Command/releases?per_page=100");
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "api.github.com", "github.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com", "objects.githubusercontent.com" };
    private readonly HttpClient _client;
    public UpdaterClient(HttpClient client) { _client = client; }

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCore-Command-Updater/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task<InstallerRelease?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await GetSafeAsync(Releases, cancellationToken);
        byte[] data = await ReadLimitedAsync(response, 2_000_000, cancellationToken);
        using var document = JsonDocument.Parse(data);
        return ReleaseSelector.Select(document.RootElement, currentVersion);
    }

    public async Task<string> DownloadAsync(string currentVersion, string expectedVersion, string temporaryRoot, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        CancellationToken token = deadline.Token;
        var release = await CheckAsync(currentVersion, token);
        if (release is null || release.Version != expectedVersion) throw new InvalidOperationException("The available release changed; check again before updating.");
        string directory = Path.Combine(Path.GetFullPath(temporaryRoot), "meshcore-command-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, release.Name);
        try
        {
            using var response = await GetSafeAsync(release.Url, token);
            if (response.Content.Headers.ContentLength is long announced && announced != release.Size) throw new InvalidDataException("Installer size does not match the release.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920]; int read; long total = 0;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                total += read;
                if (total > release.Size || total > ReleaseSelector.MaximumInstallerBytes) throw new InvalidDataException("Installer is larger than expected.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer failed its SHA-256 check.");
            return path;
        }
        catch (Exception error)
        {
            try { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); } catch (IOException) { }
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("The update download timed out.", error);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetSafeAsync(Uri url, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(url.Host) || !url.IsDefaultPort || url.UserInfo.Length != 0)
                throw new InvalidDataException("GitHub redirected the download to an untrusted address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is Uri final && (final.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(final.Host) || !final.IsDefaultPort || final.UserInfo.Length != 0))
            { response.Dispose(); throw new InvalidDataException("Download ended at an untrusted address."); }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Download redirect has no destination.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Too many download redirects.");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Release information is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        { if (output.Length + read > limit) throw new InvalidDataException("Release information is too large."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}
