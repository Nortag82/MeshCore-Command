using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>Activates a verified staged package without overwriting an existing version.</summary>
public static class VersionedInstall
{
    private sealed record CurrentVersion(int Format, string Version, string Executable);

    public static async Task ActivateAsync(string installRoot, string stagedDirectory, string version, string executable, CancellationToken cancellationToken = default)
    {
        string root = Root(installRoot);
        ValidateVersion(version);
        if (!Regex.IsMatch(executable, "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,100}\\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || executable.EndsWith(' '))
            throw new InvalidDataException("Invalid application executable name.");
        string staging = Path.Combine(root, "staging");
        string staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedDirectory));
        if (!string.Equals(Path.GetDirectoryName(staged), staging, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(staged).StartsWith("mcc-stage-", StringComparison.Ordinal))
            throw new InvalidDataException("The package must be staged inside the installation directory.");
        EnsureDirectory(root); EnsureDirectory(staging); EnsureDirectory(staged);
        using var installLock = AcquireLock(root);
        string sourceExecutable = Path.Combine(staged, executable);
        if (!File.Exists(sourceExecutable) || (File.GetAttributes(sourceExecutable) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The staged application executable is missing or is a link.");
        string versions = Path.Combine(root, "versions"); Directory.CreateDirectory(versions); EnsureDirectory(versions);
        string destination = Path.Combine(versions, version);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("This version is already installed.");
        string pointer = Path.Combine(root, "current.json");
        byte[]? oldPointer = null;
        if (File.Exists(pointer))
        {
            _ = ResolvePointer(root, "current.json");
            oldPointer = await File.ReadAllBytesAsync(pointer, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // A crash here leaves an unused version; it never changes the current pointer.
        Directory.Move(staged, destination);
        try
        {
            if (oldPointer is not null) await WritePointerAsync(root, "previous.json", oldPointer, cancellationToken);
            await WritePointerAsync(root, "current.json", JsonSerializer.SerializeToUtf8Bytes(new CurrentVersion(1, version, executable)), cancellationToken);
        }
        catch (Exception error)
        {
            // This destination was freshly created by the move above; restore the verified
            // staging directory so a failed activation can be retried without deleting data.
            try { Directory.Move(destination, staged); }
            catch (Exception rollback) { throw new AggregateException("Activation failed and the staged version could not be returned.", error, rollback); }
            throw;
        }
    }

    public static async Task RollbackAsync(string installRoot, CancellationToken cancellationToken = default)
    {
        string root = Root(installRoot); EnsureDirectory(root);
        using var installLock = AcquireLock(root);
        _ = ResolvePointer(root, "current.json");
        _ = ResolvePointer(root, "previous.json");
        byte[] current = await File.ReadAllBytesAsync(Path.Combine(root, "current.json"), cancellationToken);
        byte[] previous = await File.ReadAllBytesAsync(Path.Combine(root, "previous.json"), cancellationToken);
        await WritePointerAsync(root, "current.json", previous, cancellationToken);
        // If this second write fails the application still points at the restored version.
        await WritePointerAsync(root, "previous.json", current, cancellationToken);
    }

    private static async Task WritePointerAsync(string root, string name, byte[] contents, CancellationToken cancellationToken)
    {
        string pointer = Path.Combine(root, name);
        if (File.Exists(pointer) && (File.GetAttributes(pointer) & FileAttributes.ReparsePoint) != 0) throw new IOException("Installed version information cannot be a link.");
        string temporary = Path.Combine(root, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(contents, cancellationToken); file.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, pointer, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string ResolveCurrent(string installRoot) => ResolvePointer(Root(installRoot), "current.json");

    public static bool CanRecoverPrevious(string installRoot)
    {
        try { _ = ResolvePointer(Root(installRoot), "previous.json"); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException) { return false; }
    }

    public static async Task RecoverPreviousAsync(string installRoot, CancellationToken cancellationToken = default)
    {
        string root = Root(installRoot); EnsureDirectory(root);
        using var installLock = AcquireLock(root);
        _ = ResolvePointer(root, "previous.json");
        byte[] previous = await File.ReadAllBytesAsync(Path.Combine(root, "previous.json"), cancellationToken);
        await WritePointerAsync(root, "current.json", previous, cancellationToken);
    }

    private static string ResolvePointer(string root, string name)
    {
        EnsureDirectory(root);
        string pointer = Path.Combine(root, name);
        if (!File.Exists(pointer) || (File.GetAttributes(pointer) & FileAttributes.ReparsePoint) != 0 || new FileInfo(pointer).Length > 4096) throw new InvalidDataException("Installed version information is missing or invalid.");
        var current = JsonSerializer.Deserialize<CurrentVersion>(File.ReadAllText(pointer)) ?? throw new InvalidDataException("Installed version information is invalid.");
        if (current.Format != 1) throw new InvalidDataException("Unsupported installed version information.");
        ValidateVersion(current.Version);
        if (current.Executable is null || !Regex.IsMatch(current.Executable, "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,100}\\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid application executable name.");
        string versions = Path.Combine(root, "versions"), directory = Path.Combine(versions, current.Version);
        EnsureDirectory(versions); EnsureDirectory(directory);
        string path = Path.Combine(directory, current.Executable);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Installed application is missing or is a link.");
        return path;
    }

    private static string Root(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A filesystem root cannot be used as the installation directory.");
        return root;
    }
    private static void ValidateVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, "^(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid version identifier.");
    }
    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Installation directory is missing or is a link.");
    }
    private static FileStream AcquireLock(string root)
    {
        string lockPath = Path.Combine(root, ".install.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Install lock cannot be a link.");
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
