using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>Stages and verifies an installation package without changing the installed application.</summary>
public static class InstallPackage
{
    private const long MaxTotalBytes = 1_073_741_824;
    private sealed record PackageManifest(int Version, Dictionary<string, string> Files);

    public static async Task<string> StageAsync(string packageFile, string stagingRoot, CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A filesystem root cannot be used as a staging directory.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("The staging directory cannot be a link.");
        string stage = Path.GetFullPath(Path.Combine(root, "mcc-stage-" + Guid.NewGuid().ToString("N")));
        if (!string.Equals(Path.GetDirectoryName(stage), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid staging path.");
        using var zip = ZipFile.OpenRead(packageFile);
        if (zip.Entries.Count is < 2 or > 10002) throw new InvalidDataException("Invalid installer package.");
        var manifestEntries = zip.Entries.Where(e => e.FullName == "manifest.json").ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].Length > 2_000_000) throw new InvalidDataException("Invalid installer manifest.");
        PackageManifest manifest;
        using (var stream = manifestEntries[0].Open())
            manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken) ?? throw new InvalidDataException("Invalid installer manifest.");
        if (manifest.Version != 1 || manifest.Files is null || manifest.Files.Count is < 1 or > 10000) throw new InvalidDataException("Unsupported installer manifest.");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            ValidateName(item.Key);
            if (item.Value is null || !Regex.IsMatch(item.Value, "^[0-9a-f]{64}$") || !expected.TryAdd(item.Key, item.Value)) throw new InvalidDataException("Invalid installer manifest entry.");
        }
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json"))
        {
            ValidateName(entry.FullName);
            if (!expected.ContainsKey(entry.FullName) || !entries.TryAdd(entry.FullName, entry) || entry.Length < 0 || entry.Length > MaxTotalBytes - total)
                throw new InvalidDataException("Unexpected, duplicate or oversized installer file.");
            total += entry.Length;
        }
        if (entries.Count != expected.Count) throw new InvalidDataException("Installer package is incomplete.");
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var (name, entry) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string outputPath = Path.GetFullPath(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!outputPath.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid installer path.");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = entry.Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                byte[] buffer = new byte[81920]; long written = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    written += read;
                    if (written > entry.Length) throw new InvalidDataException("Installer file exceeds its declared size.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (written != entry.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected[name], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installer file failed its integrity check.");
            }
            return stage;
        }
        catch
        {
            // stage is a fresh direct child of the verified staging root, never the installed app.
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(stage)), root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.EndsWith('/') ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(ch => char.IsControl(ch) || "<>\"|?*".Contains(ch)) ||
                Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Invalid installer path.");
    }
}
