using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record MercuryWindowsRelease(Version Version, Uri ReleasePage, string? ArchiveSha256, Uri ArchiveUrl, long ArchiveSize);

public sealed class MercuryReleaseClient(HttpClient client)
{
    private const string Repository = "Rhizomatica/mercury";
    private static readonly Uri Releases = new($"https://api.github.com/repos/{Repository}/releases?per_page=30");
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");
    private static readonly Regex WindowsArchive = new(@"\Amercury-(?<version>[0-9]+\.[0-9]+\.[0-9]+)-w64-[0-9a-f]{7,40}\.zip\z");
    private static readonly Regex Digest = new(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase);
    public const int MaximumArchiveBytes = 64 * 1024 * 1024;

    public async Task<MercuryWindowsRelease> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await UpdaterClient.GetSafeAsync(client, Releases, cancellationToken);
        byte[] data = await UpdaterClient.ReadLimitedAsync(response, 1_000_000, cancellationToken);
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned unexpected Mercury release information.");

        MercuryWindowsRelease? latest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || ReleaseSelector.IsTrue(item, "draft") ||
                ReleaseSelector.IsTrue(item, "prerelease") || string.IsNullOrEmpty(ReleaseSelector.StringProperty(item, "published_at"))) continue;
            string? tag = ReleaseSelector.StringProperty(item, "tag_name");
            var version = ReleaseSelector.Parse(tag);
            if (version is null || (latest is not null && version <= latest.Version) ||
                !item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || ReleaseSelector.StringProperty(asset, "state") != "uploaded") continue;
                string name = ReleaseSelector.StringProperty(asset, "name") ?? "";
                var match = WindowsArchive.Match(name);
                if (!match.Success || match.Groups["version"].Value != version.ToString(3)) continue;
                string? checksum = ReleaseSelector.StringProperty(asset, "digest");
                long size = asset.TryGetProperty("size", out var length) && length.ValueKind == JsonValueKind.Number &&
                    length.TryGetInt64(out long bytes) ? bytes : 0;
                latest = new(version, new Uri($"{ReleasesPage}/tag/{tag}"),
                    checksum is not null && Digest.IsMatch(checksum) ? checksum[7..] : null,
                    new Uri($"{ReleasesPage}/download/{tag}/{name}"), size);
                break;
            }
        }
        return latest ?? throw new InvalidDataException("GitHub returned no stable Mercury release with a Windows x64 archive.");
    }

    public async Task DownloadAsync(MercuryWindowsRelease release, string path, CancellationToken stop)
    {
        if (release.ArchiveSha256 is null || release.ArchiveSize is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("GitHub did not provide a usable size and SHA-256 checksum for the Mercury archive.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        CancellationToken token = deadline.Token;
        using var response = await UpdaterClient.GetSafeAsync(client, release.ArchiveUrl, token);
        if (response.Content.Headers.ContentLength is long length && length != release.ArchiveSize)
            throw new InvalidDataException("The Mercury archive size does not match the release.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > release.ArchiveSize) throw new InvalidDataException("The Mercury archive is larger than expected.");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != release.ArchiveSize ||
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Mercury archive failed its SHA-256 check.");
    }
}
