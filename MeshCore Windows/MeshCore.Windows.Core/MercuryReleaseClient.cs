using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record MercuryWindowsRelease(Version Version, Uri ReleasePage, string? ArchiveSha256);

public sealed class MercuryReleaseClient(HttpClient client)
{
    private const string Repository = "Rhizomatica/mercury";
    private static readonly Uri Releases = new($"https://api.github.com/repos/{Repository}/releases?per_page=30");
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");
    private static readonly Regex WindowsArchive = new(@"\Amercury-(?<version>[0-9]+\.[0-9]+\.[0-9]+)-w64-[0-9a-f]{7,40}\.zip\z");
    private static readonly Regex Digest = new(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.IgnoreCase);

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
                var match = WindowsArchive.Match(ReleaseSelector.StringProperty(asset, "name") ?? "");
                if (!match.Success || match.Groups["version"].Value != version.ToString(3)) continue;
                string? checksum = ReleaseSelector.StringProperty(asset, "digest");
                latest = new(version, new Uri($"{ReleasesPage}/tag/{tag}"),
                    checksum is not null && Digest.IsMatch(checksum) ? checksum[7..] : null);
                break;
            }
        }
        return latest ?? throw new InvalidDataException("GitHub returned no stable Mercury release with a Windows x64 archive.");
    }
}
