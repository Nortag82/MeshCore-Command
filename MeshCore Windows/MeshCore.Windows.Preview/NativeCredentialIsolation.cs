using System.Text;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

/// <summary>Stages an independent credential copy before selecting it for a native profile.</summary>
internal static class NativeCredentialIsolation
{
    private const string MarkerContents = "MeshCore native credential isolation v1\n";

    internal static async Task EnsureWithLegacyLockAsync(string registry, string legacyProfile, ICredentialStore source, ICredentialStore destination, CancellationToken cancellationToken = default)
    {
        string marker = Path.Combine(Path.GetFullPath(registry), "credentials-isolated-v1");
        // Once selected, the native store is independent and the old profile no longer needs locking.
        if (File.Exists(marker)) { await EnsureAsync(registry, source, destination, cancellationToken); return; }
        string profile = Path.GetFullPath(legacyProfile);
        if (!Directory.Exists(profile))
        {
            if (source.ListSaved().Count != 0) throw new IOException("Cannot verify the old profile is closed. Restore a backup or locate the old profile before copying credentials.");
            await EnsureAsync(registry, source, destination, cancellationToken);
            return;
        }
        if ((File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0) throw new IOException("The old profile cannot be a link.");
        string lockPath = Path.Combine(profile, "Default", "Local Storage", "leveldb", "LOCK");
        if (!File.Exists(lockPath) || (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cannot verify the old profile is closed. Close the old app and retry.");
        using var locked = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        locked.Lock(0, 1);
        try { await EnsureAsync(registry, source, destination, cancellationToken); }
        finally { locked.Unlock(0, 1); }
    }

    // Caller holds the native profile lease and must ensure the legacy app is closed.
    internal static async Task EnsureAsync(string registry, ICredentialStore source, ICredentialStore destination, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(registry);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid native data directory.");
        string marker = Path.Combine(root, "credentials-isolated-v1");
        if (File.Exists(marker))
        {
            var info = new FileInfo(marker);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 128 || await File.ReadAllTextAsync(marker, cancellationToken) != MarkerContents)
                throw new InvalidDataException("Native credential selection is invalid; no credentials were changed.");
            return;
        }
        // Preserve the old values before copying. Repeated attempts create additional recoverable snapshots.
        _ = await CredentialRecovery.CreateAsync(root, source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var before = source.ListSaved().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        CredentialNamespaceMigration.Copy(source, destination);
        var after = source.ListSaved();
        if (before.Count != after.Count || before.Any(item => !after.TryGetValue(item.Key, out string? value) || value != item.Value) || after.Any(item => {
            string[] parts = item.Key.Split('/');
            return parts.Length != 2 || destination.Get(parts[0], parts[1]) != item.Value;
        })) throw new IOException("Legacy credentials changed during copying. Close the old app and retry.");
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(root, ".credential-selection-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(MarkerContents);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(bytes, cancellationToken); file.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, marker, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
