using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace MeshCore.Windows.Core;

/// <summary>Stages a closed legacy profile and its desktop data without modifying either source.</summary>
[SupportedOSPlatform("windows")]
public static class LegacyMigration
{
    public static async Task StageAsync(string legacyWebView, string legacyData, string destination, CancellationToken cancellationToken = default, Func<CancellationToken, Task>? beforeComplete = null)
    {
        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyWebView));
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyData));
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        static bool Inside(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (new[] { profile, data, target }.Any(path => path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("A filesystem root is not a migration directory.");
        if (Inside(profile, target) || Inside(target, profile) || Inside(data, target) || Inside(target, data) || Inside(profile, data) || Inside(data, profile)) throw new ArgumentException("Migration directories must be separate.");
        if (!Directory.Exists(data) || (File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0) throw new IOException("The legacy data directory is missing or is a link.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Migration destination already exists.");
        Directory.CreateDirectory(target);
        string marker = Path.Combine(target, ".mcc-migration-incomplete");
        await File.WriteAllTextAsync(marker, "Incomplete migration; do not use.", cancellationToken);
        await ProfileSnapshot.CopyAsync(profile, Path.Combine(target, "WebView", "EBWebView"), cancellationToken, async lockToken => {
        lockToken.ThrowIfCancellationRequested();
        string dataTarget = Path.Combine(target, "Data");
        Directory.CreateDirectory(dataTarget);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((data, dataTarget));
        while (pending.TryPop(out var current))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Migration cannot follow links or junctions.");
                string output = Path.Combine(current.Destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) { Directory.CreateDirectory(output); pending.Push((entry, output)); continue; }
                await using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var copy = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                { await input.CopyToAsync(copy, cancellationToken); await copy.FlushAsync(cancellationToken); }
                await using var original = File.OpenRead(entry);
                await using var copied = File.OpenRead(output);
                byte[] originalHash = await SHA256.HashDataAsync(original, cancellationToken);
                byte[] copiedHash = await SHA256.HashDataAsync(copied, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(originalHash, copiedHash))
                    throw new IOException("A data file changed while preparing migration.");
            }
        }
        string boards = Path.Combine(dataTarget, "boards.json");
        if (File.Exists(boards)) _ = new BoardProfiles(boards);
        string mapConfig = Path.Combine(dataTarget, "offline-map.json");
        if (File.Exists(mapConfig))
        {
            var map = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(mapConfig, cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("Invalid offline map configuration.");
            string configured = map["path"]?.GetValue<string>() ?? throw new InvalidDataException("Offline map path is missing.");
            if (!Path.IsPathFullyQualified(configured)) throw new InvalidDataException("Offline map path must be absolute.");
            string mapPath = Path.GetFullPath(configured);
            if (Inside(data, mapPath))
            {
                string copiedMap = Path.GetFullPath(Path.Combine(dataTarget, Path.GetRelativePath(data, mapPath)));
                if (!Inside(dataTarget, copiedMap) || !File.Exists(copiedMap)) throw new InvalidDataException("The selected offline map could not be staged.");
                map["path"] = copiedMap;
                await File.WriteAllTextAsync(mapConfig, map.ToJsonString(), cancellationToken);
            }
        }
        if (beforeComplete is not null) await beforeComplete(cancellationToken);
        });
        File.Delete(marker);
    }
}
