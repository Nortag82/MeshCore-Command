using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record ValidatedBackup(JsonElement Manifest, IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Read-only validation of an existing full-backup ZIP; never applies data.</summary>
public static class BackupArchive
{
    private static readonly Regex MapName = new("^maps/[0-9a-f]{64}\\.(mbtiles|pmtiles)$", RegexOptions.CultureInvariant);
    private static readonly Regex CredentialName = new("^(secure|repeater)/[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private const long UncompressedLimit = BackupEnvelope.MaximumArchiveBytes + 32L * 1024 * 1024;

    /// <summary>Builds an archive from an explicit snapshot, without reading a user profile.</summary>
    public static byte[] Create(IReadOnlyDictionary<string, string> storage, IReadOnlyDictionary<string, string> credentials,
        IReadOnlyDictionary<string, byte[]> files, string? selected = null, JsonElement? radio = null,
        string? boardProfiles = null, string? serialProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(files);
        var safeStorage = storage.Where(item => item.Key != "rcc6-session" && item.Key != "rcc6-radio-owner" &&
            !item.Key.StartsWith("rcc6-session:", StringComparison.Ordinal) && !item.Key.StartsWith("rcc6-radio-owner:", StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, storage = safeStorage, credentials, selected, radio, boardProfiles, serialProfiles });
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteEntry(zip, "manifest.json", manifest);
            long total = 0;
            foreach (var file in files)
            {
                if (file.Value is null || file.Key == "manifest.json") throw new InvalidDataException("Invalid backup file.");
                if (file.Value.LongLength > BackupEnvelope.MaximumArchiveBytes - total) throw new InvalidDataException("Backup files are too large.");
                total += file.Value.LongLength;
                WriteEntry(zip, file.Key, file.Value);
            }
        }
        if (output.Length > BackupEnvelope.MaximumArchiveBytes) throw new InvalidDataException("Backup is too large.");
        byte[] archive = output.ToArray();
        // Apply the same name, map integrity, credential and size checks to exports and imports.
        Validate(archive);
        return archive;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        using var destination = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        destination.Write(data);
    }

    public static ValidatedBackup Validate(byte[] archive)
    {
        if (archive.Length > BackupEnvelope.MaximumArchiveBytes) throw new InvalidDataException("Backup is too large.");
        using var stream = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10002) throw new InvalidDataException("Too many backup files.");
        long total = 0;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name != "manifest.json" && name != "devices.json" && !MapName.IsMatch(name)) throw new InvalidDataException("Invalid backup file name.");
            if (files.ContainsKey(name)) throw new InvalidDataException("Duplicate backup file.");
            if (entry.Length < 0 || entry.Length > UncompressedLimit - total) throw new InvalidDataException("Backup contents are too large.");
            total += entry.Length;
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > entry.Length) throw new InvalidDataException("Invalid backup entry size.");
                output.Write(buffer, 0, count);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("Incomplete backup entry.");
            var data = output.ToArray();
            if (name.StartsWith("maps/", StringComparison.Ordinal) && !Convert.ToHexString(SHA256.HashData(data)).Equals(name.Substring(5, 64), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Offline map failed its integrity check.");
            files.Add(name, data);
        }
        if (!files.Remove("manifest.json", out byte[]? manifest)) throw new InvalidDataException("Backup manifest is missing.");
        using var doc = JsonDocument.Parse(manifest);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
            throw new InvalidDataException("Unsupported backup version.");
        if (!root.TryGetProperty("storage", out var storage) || storage.ValueKind != JsonValueKind.Object || storage.EnumerateObject().Count() > 10000 ||
            storage.EnumerateObject().Any(p => p.Name.Length > 512 || p.Value.ValueKind != JsonValueKind.String)) throw new InvalidDataException("Invalid app storage.");
        if (System.Text.Encoding.UTF8.GetByteCount(storage.GetRawText()) > 32 * 1024 * 1024) throw new InvalidDataException("App data is too large.");
        if (!root.TryGetProperty("credentials", out var credentials) || credentials.ValueKind != JsonValueKind.Object || credentials.EnumerateObject().Count() > 10000 ||
            credentials.EnumerateObject().Any(p => !CredentialName.IsMatch(p.Name) || p.Value.ValueKind != JsonValueKind.String || System.Text.Encoding.UTF8.GetByteCount(p.Value.GetString()!) is < 1 or > 512))
            throw new InvalidDataException("Invalid backup credentials.");
        if (root.TryGetProperty("selected", out var selected) && selected.ValueKind != JsonValueKind.Null &&
            (selected.ValueKind != JsonValueKind.String || !files.ContainsKey(selected.GetString()!))) throw new InvalidDataException("Selected offline map is missing.");
        if (root.TryGetProperty("radio", out var radio) && radio.ValueKind != JsonValueKind.Null &&
            (radio.ValueKind != JsonValueKind.Object || !radio.TryGetProperty("version", out var rv) || rv.ValueKind != JsonValueKind.Number || !rv.TryGetInt32(out int rvi) || rvi != 1 || System.Text.Encoding.UTF8.GetByteCount(radio.GetRawText()) > 2_000_000))
            throw new InvalidDataException("Invalid radio backup.");
        foreach (var (name, serial) in new[] { ("boardProfiles", false), ("serialProfiles", true) })
            if (root.TryGetProperty(name, out var profiles) && profiles.ValueKind != JsonValueKind.Null)
            {
                if (profiles.ValueKind != JsonValueKind.String || System.Text.Encoding.UTF8.GetByteCount(profiles.GetString()!) > 1_000_000) throw new InvalidDataException("Invalid saved board profiles.");
                try { BoardProfiles.ValidateSerialized(profiles.GetString()!, serial); }
                catch (JsonException error) { throw new InvalidDataException("Invalid saved board profiles.", error); }
            }
        if (files.TryGetValue("devices.json", out byte[]? devices)) { using var ignored = JsonDocument.Parse(devices); }
        return new ValidatedBackup(root.Clone(), files);
    }
}
