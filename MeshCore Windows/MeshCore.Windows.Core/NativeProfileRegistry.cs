using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record NativeProfile(string Directory, string WebView, string Data, int Port);

/// <summary>Safely selects an immutable-location migrated profile without altering legacy data.</summary>
[SupportedOSPlatform("windows")]
public static class NativeProfileRegistry
{
    private sealed record Pointer(int Format, string Id, int Port);

    public static async Task<NativeProfile> PrepareAsync(string registryDirectory, string legacyWebView, string legacyData, int port = 19483, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeCommit = null)
    {
        string root = Root(registryDirectory);
        static bool Inside(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string oldProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyWebView));
        string oldData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyData));
        if (Inside(oldProfile, root) || Inside(root, oldProfile) || Inside(oldData, root) || Inside(root, oldData))
            throw new ArgumentException("The native profile registry must be separate from legacy data.");
        Directory.CreateDirectory(root); EnsureDirectory(root);
        string lockFile = Path.Combine(root, ".profile.lock");
        if (File.Exists(lockFile) && (File.GetAttributes(lockFile) & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile lock cannot be a link.");
        using var locked = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string pointer = Path.Combine(root, "current.json");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (File.Exists(pointer))
        {
            var existing = Open(root);
            if (existing.Port != port) throw new InvalidDataException("The selected profile uses a different local origin.");
            return existing;
        }
        string profiles = Path.Combine(root, "profiles"); Directory.CreateDirectory(profiles); EnsureDirectory(profiles);
        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(profiles, id);
        await LegacyMigration.StageAsync(legacyWebView, legacyData, destination, cancellationToken, beforeCommit);
        NativeProfile result = Validate(root, id, port);
        string temporary = Path.Combine(root, ".profile-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] contents = JsonSerializer.SerializeToUtf8Bytes(new Pointer(1, id, port));
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await output.WriteAsync(contents, cancellationToken); output.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, pointer, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }

    public static NativeProfile Open(string registryDirectory)
    {
        string root = Root(registryDirectory); EnsureDirectory(root);
        string path = Path.Combine(root, "current.json");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 4096)
            throw new InvalidDataException("Native profile selection is missing or invalid.");
        var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid native profile selection.");
        if (pointer.Format != 1) throw new InvalidDataException("Unsupported native profile selection.");
        return Validate(root, pointer.Id, pointer.Port);
    }

    /// <summary>Creates an empty profile only when explicitly requested; never replaces an existing selection.</summary>
    public static async Task<NativeProfile> CreateEmptyAsync(string registryDirectory, int port = 19483, CancellationToken cancellationToken = default)
    {
        string root = Root(registryDirectory);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Directory.CreateDirectory(root); EnsureDirectory(root);
        string lockFile = Path.Combine(root, ".profile.lock");
        if (File.Exists(lockFile) && (File.GetAttributes(lockFile) & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile lock cannot be a link.");
        using var locked = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string pointer = Path.Combine(root, "current.json");
        if (File.Exists(pointer)) throw new IOException("A native profile is already selected; it was not changed.");
        string profiles = Path.Combine(root, "profiles"); Directory.CreateDirectory(profiles); EnsureDirectory(profiles);
        string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(profiles, id);
        Directory.CreateDirectory(Path.Combine(directory, "WebView", "EBWebView"));
        Directory.CreateDirectory(Path.Combine(directory, "Data"));
        var profile = Validate(root, id, port);
        string temporary = Path.Combine(root, ".profile-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] contents = JsonSerializer.SerializeToUtf8Bytes(new Pointer(1, id, port));
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await output.WriteAsync(contents, cancellationToken); output.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, pointer, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return profile;
    }

    private static NativeProfile Validate(string root, string id, int port)
    {
        if (id is null || !Regex.IsMatch(id, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid native profile identifier.");
        if (port is < 1 or > 65535) throw new InvalidDataException("Invalid native profile origin.");
        string profiles = Path.Combine(root, "profiles"); EnsureDirectory(profiles);
        string profile = Path.Combine(profiles, id); EnsureDirectory(profile);
        if (File.Exists(Path.Combine(profile, ".mcc-migration-incomplete"))) throw new InvalidDataException("The migrated profile is incomplete.");
        string webView = Path.Combine(profile, "WebView"), data = Path.Combine(profile, "Data");
        EnsureDirectory(webView); EnsureDirectory(Path.Combine(webView, "EBWebView")); EnsureDirectory(data);
        if (File.Exists(Path.Combine(webView, "EBWebView", ".mcc-incomplete"))) throw new InvalidDataException("The browser profile is incomplete.");
        return new NativeProfile(profile, webView, data, port);
    }
    private static string Root(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A filesystem root cannot be used as a profile registry.");
        return root;
    }
    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile directory is missing or is a link.");
    }
}
