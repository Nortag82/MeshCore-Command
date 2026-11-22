using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace MeshCore.Windows.Core;

/// <summary>Creates a verified copy of a closed legacy WebView2 profile without modifying it.</summary>
[SupportedOSPlatform("windows")]
public static class ProfileSnapshot
{
    public static async Task CopyAsync(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? whileLocked = null)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (source.Equals(Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase) || destination.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A filesystem root cannot be used as a profile or snapshot.");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The existing browser profile was not found.");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The snapshot destination already exists.");
        if (Contains(source, destination) || Contains(destination, source)) throw new ArgumentException("Profile and snapshot paths must be separate.");
        EnsureNotLink(source);
        string lockPath = Path.Combine(source, "Default", "Local Storage", "leveldb", "LOCK");
        if (!File.Exists(lockPath)) throw new IOException("Cannot verify that the existing profile is closed; use a full backup instead.");
        using var profileLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        profileLock.Lock(0, 1);
        try
        {
            Directory.CreateDirectory(destination);
            string marker = Path.Combine(destination, ".mcc-incomplete");
            await File.WriteAllTextAsync(marker, "Incomplete profile snapshot; do not use.", cancellationToken);
            var pending = new Stack<(string Source, string Destination)>(); pending.Push((source, destination));
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFullPath(entry), lockPath, StringComparison.OrdinalIgnoreCase)) continue;
                    EnsureNotLink(entry);
                    string target = Path.Combine(directory.Destination, Path.GetFileName(entry));
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    { Directory.CreateDirectory(target); pending.Push((entry, target)); continue; }
                    await using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    { await input.CopyToAsync(output, cancellationToken); await output.FlushAsync(cancellationToken); }
                    await using var original = File.OpenRead(entry);
                    await using var copy = File.OpenRead(target);
                    byte[] a = await SHA256.HashDataAsync(original, cancellationToken), b = await SHA256.HashDataAsync(copy, cancellationToken);
                    if (!CryptographicOperations.FixedTimeEquals(a, b)) throw new IOException("A profile file changed while the snapshot was being made.");
                }
            }
            if (whileLocked is not null) await whileLocked(cancellationToken);
            File.Delete(marker);
        }
        finally { profileLock.Unlock(0, 1); }
    }

    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile snapshots cannot follow links or junctions.");
    }
}
