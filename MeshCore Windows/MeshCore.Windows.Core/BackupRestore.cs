using System.Security.Cryptography;
using System.Text.Json;

namespace MeshCore.Windows.Core;

public interface IBackupCredentials
{
    string? Get(string scope, string key);
    void Put(string scope, string key, string value);
    void Delete(string scope, string key);
}

public interface ICredentialStore : IBackupCredentials
{
    IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null);
}

/// <summary>Applies validated file and credential data with rollback; UI storage and radio restore are separate.</summary>
public static class BackupRestore
{
    public static void Apply(byte[] archive, string directory, IBackupCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var backup = BackupArchive.Validate(archive);
        string root = Path.GetFullPath(directory);
        var undoFiles = new List<(string Path, byte[]? Data)>();
        var undoCredentials = new List<(string Scope, string Key, string? Value)>();

        void Write(string path, byte[] data)
        {
            byte[]? previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
            AtomicWrite(path, data);
            undoFiles.Add((path, previous));
        }

        try
        {
            if (backup.Files.TryGetValue("devices.json", out var devices)) Write(Path.Combine(root, "devices.json"), devices);
            string mercuryDirectory = Path.Combine(root, "mercury");
            if ((backup.Files.ContainsKey(HfLinkBackup.Mercury) || backup.Files.ContainsKey(HfLinkBackup.ExternalMercury)) &&
                Directory.Exists(mercuryDirectory) && (File.GetAttributes(mercuryDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Mercury configuration directory cannot be a link.");
            if (backup.Files.TryGetValue(HfLinkBackup.Mercury, out var mercury)) Write(HfLinkBackup.BundledConfig(root), mercury);
            string? externalConfig = null;
            if (backup.Files.TryGetValue(HfLinkBackup.ExternalMercury, out var external))
            {
                externalConfig = HfLinkBackup.ExternalConfig(root);
                Write(externalConfig, external);
            }
            if (backup.Files.TryGetValue(HfLinkBackup.History, out var hfLink))
                Write(Path.Combine(root, HfLink.DatabaseName), HfLinkBackup.RestoreDatabase(root, hfLink, externalConfig));
            foreach (var (property, filename) in new[] { ("boardProfiles", "boards.json"), ("serialProfiles", "serial-boards.json") })
                if (backup.Manifest.TryGetProperty(property, out var profiles) && profiles.ValueKind == JsonValueKind.String)
                    Write(Path.Combine(root, filename), System.Text.Encoding.UTF8.GetBytes(profiles.GetString()!));
            foreach (var (name, data) in backup.Files.Where(entry => entry.Key.StartsWith("maps/", StringComparison.Ordinal)))
            {
                string target = Path.Combine(root, "offline-maps", Path.GetFileName(name));
                if (!File.Exists(target)) Write(target, data);
                else
                {
                    using var input = File.OpenRead(target);
                    if (!Convert.ToHexString(SHA256.HashData(input)).Equals(name.Substring(5, 64), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Existing offline map conflicts with backup.");
                }
            }
            if (backup.Manifest.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.String)
            {
                string name = selected.GetString()!;
                if (!name.StartsWith("maps/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid selected map.");
                string path = Path.Combine(root, "offline-maps", Path.GetFileName(name));
                Write(Path.Combine(root, "offline-map.json"), JsonSerializer.SerializeToUtf8Bytes(new { path }));
            }
            foreach (var item in backup.Manifest.GetProperty("credentials").EnumerateObject())
            {
                string[] parts = item.Name.Split('/');
                string? previous = credentials.Get(parts[0], parts[1]);
                // Record before writing: a failing backend may have already changed the credential.
                undoCredentials.Add((parts[0], parts[1], previous));
                credentials.Put(parts[0], parts[1], item.Value.GetString()!);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in undoCredentials.AsEnumerable().Reverse())
            {
                try { if (item.Value is null) credentials.Delete(item.Scope, item.Key); else credentials.Put(item.Scope, item.Key, item.Value); }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            foreach (var item in undoFiles.AsEnumerable().Reverse())
            {
                try { if (item.Data is null) File.Delete(item.Path); else AtomicWrite(item.Path, item.Data); }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            if (rollbackErrors.Count != 0) throw new AggregateException("Restore failed and rollback was incomplete; use the recovery backup.", new[] { error }.Concat(rollbackErrors));
            throw;
        }
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(data); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
