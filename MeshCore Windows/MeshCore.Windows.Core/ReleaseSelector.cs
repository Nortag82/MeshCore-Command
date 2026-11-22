using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record InstallerRelease(string Version, bool Prerelease, Uri Url, string Sha256, long Size, string Name);

/// <summary>Validates an official GitHub release before an update can be downloaded.</summary>
public static class ReleaseSelector
{
    public const long MaximumInstallerBytes = 200L * 1024 * 1024;

    private static Version? Parse(string? text)
    {
        if (text is null || !Regex.IsMatch(text, "^v?(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")) return null;
        if (text.StartsWith('v')) text = text[1..];
        return Version.TryParse(text, out var version) && version.Build >= 0 ? version : null;
    }

    public static InstallerRelease? Select(JsonElement releases, string current)
    {
        var currentVersion = Parse(current) ?? throw new ArgumentException("Invalid current version.", nameof(current));
        if (releases.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid release information.");
        Version? newest = null; JsonElement chosen = default;
        foreach (var item in releases.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || IsTrue(item, "draft") || !item.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(published.GetString()) ||
                !item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
            var version = Parse(tag.GetString());
            if (version is null || version <= currentVersion || (newest is not null && version <= newest)) continue;
            string candidateName = $"MeshCore-Command-Native-Windows-{version.ToString(3)}-Setup.exe";
            if (!item.TryGetProperty("assets", out var candidateAssets) || candidateAssets.ValueKind != JsonValueKind.Array ||
                !candidateAssets.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object && StringProperty(a, "name") == candidateName)) continue;
            newest = version; chosen = item;
        }
        if (newest is null) return null;
        string versionText = newest.ToString(3), tagText = chosen.GetProperty("tag_name").GetString()!;
        // Native builds must never offer the older Python-host installer as an update.
        string name = $"MeshCore-Command-Native-Windows-{versionText}-Setup.exe";
        if (!chosen.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Release has no installer assets.");
        var matches = assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && StringProperty(a, "name") == name && StringProperty(a, "state") == "uploaded").ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Release does not contain exactly one compatible installer.");
        var asset = matches[0]; string digest = StringProperty(asset, "digest") ?? "";
        if (!Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$")) throw new InvalidDataException("Installer SHA-256 is unavailable.");
        if (!Uri.TryCreate(StringProperty(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath != $"/Nortag82/MeshCore-Command/releases/download/{tagText}/{name}") throw new InvalidDataException("Installer URL is not the expected GitHub release.");
        if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long length) || length is < 1 or > MaximumInstallerBytes)
            throw new InvalidDataException("Invalid installer size.");
        return new InstallerRelease(versionText, IsTrue(chosen, "prerelease"), url, digest[7..], length, name);
    }

    private static bool IsTrue(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? StringProperty(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
