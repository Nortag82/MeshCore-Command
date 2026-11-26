using System.Security.Cryptography;
using System.Text.Json;

namespace MeshCore.Windows.Core;

/// <summary>Collects desktop backup files from an explicitly supplied data directory.</summary>
public static class BackupSnapshot
{
    public static byte[] Create(string directory, IReadOnlyDictionary<string, string> storage,
        IReadOnlyDictionary<string, string> credentials, JsonElement? radio = null)
    {
        string root = Path.GetFullPath(directory);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string devices = Path.Combine(root, "devices.json");
        if (File.Exists(devices)) files.Add("devices.json", File.ReadAllBytes(devices));
        HfLinkBackup.Capture(root, files);
        long total = files.Values.Sum(file => file.LongLength);
        string mapDirectory = Path.Combine(root, "offline-maps");
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(mapDirectory))
            foreach (string path in Directory.EnumerateFiles(mapDirectory))
                if (IsMap(path)) maps.Add(Path.GetFullPath(path));
        string config = Path.Combine(root, "offline-map.json");
        string? selectedPath = null;
        if (File.Exists(config))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            selectedPath = Path.GetFullPath(doc.RootElement.GetProperty("path").GetString()!);
            if (!File.Exists(selectedPath) || !IsMap(selectedPath)) throw new InvalidDataException("The selected offline map is missing; re-select or remove it before exporting.");
            maps.Add(selectedPath);
        }
        string? selected = null;
        foreach (string path in maps.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (info.Length > BackupEnvelope.MaximumArchiveBytes - total) throw new InvalidDataException("Offline maps exceed the backup size limit.");
            total += info.Length;
            byte[] data = File.ReadAllBytes(path);
            if (data.LongLength != info.Length) throw new IOException("A map changed while creating the backup.");
            string name = "maps/" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + Path.GetExtension(path).ToLowerInvariant();
            files[name] = data;
            if (string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase)) selected = name;
        }
        string? Profiles(string name) { string path = Path.Combine(root, name); return File.Exists(path) ? File.ReadAllText(path) : null; }
        return BackupArchive.Create(storage, credentials, files, selected, radio, Profiles("boards.json"), Profiles("serial-boards.json"));
    }

    private static bool IsMap(string path) => Path.GetExtension(path).Equals(".mbtiles", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".pmtiles", StringComparison.OrdinalIgnoreCase);
}
