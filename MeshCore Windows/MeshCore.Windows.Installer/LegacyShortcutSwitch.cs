using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MeshCore.Windows.Installer;

/// <summary>Replaces known legacy shortcuts, preserving originals and refusing concurrent edits.</summary>
internal static class LegacyShortcutSwitch
{
    private sealed record Plan(string Target, string Temporary, string Backup, byte[] OriginalHash, byte[] NewHash);

    internal static void Apply(IEnumerable<string> shortcutPaths, string oldExecutable, string launcher, Action<int>? afterReplace = null)
    {
        string oldPath = Path.GetFullPath(oldExecutable), newPath = Path.GetFullPath(launcher);
        if (!File.Exists(newPath) || (File.GetAttributes(newPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The new launcher is missing or is a link.");
        var plans = new List<Plan>(); var replaced = new List<Plan>();
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut service is unavailable."))
                ?? throw new IOException("Windows shortcut service is unavailable.");
            foreach (string input in shortcutPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.GetFullPath(input), folder = Path.GetDirectoryName(path)!;
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A shortcut or its directory is a link.");
                object? original = null, generated = null;
                string temp = Path.Combine(folder, ".mcc-new-" + Guid.NewGuid().ToString("N") + ".lnk");
                string backup = path + ".legacy-backup";
                try
                {
                    original = ((dynamic)shell).CreateShortcut(path);
                    string actual = (string)((dynamic)original).TargetPath;
                    if (!string.Equals(Path.GetFullPath(actual), oldPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(backup)) throw new IOException("A legacy shortcut backup already exists; it was not changed.");
                    byte[] oldHash = SHA256.HashData(File.ReadAllBytes(path));
                    generated = ((dynamic)shell).CreateShortcut(temp);
                    ((dynamic)generated).TargetPath = newPath;
                    ((dynamic)generated).Arguments = "--native-candidate";
                    ((dynamic)generated).WorkingDirectory = Path.GetDirectoryName(newPath)!;
                    ((dynamic)generated).Description = "MeshCore Command";
                    ((dynamic)generated).Save();
                    plans.Add(new Plan(path, temp, backup, oldHash, SHA256.HashData(File.ReadAllBytes(temp))));
                }
                finally
                {
                    if (generated is not null && Marshal.IsComObject(generated)) Marshal.FinalReleaseComObject(generated);
                    if (original is not null && Marshal.IsComObject(original)) Marshal.FinalReleaseComObject(original);
                    if (!plans.Any(p => p.Temporary == temp) && File.Exists(temp)) File.Delete(temp);
                }
            }
            if (plans.Count == 0) throw new IOException("No shortcut pointing to the old app was found.");
            foreach (var plan in plans)
            {
                if (!CryptographicOperations.FixedTimeEquals(plan.OriginalHash, SHA256.HashData(File.ReadAllBytes(plan.Target))))
                    throw new IOException("A shortcut changed while the update was being prepared.");
                File.Replace(plan.Temporary, plan.Target, plan.Backup, ignoreMetadataErrors: false);
                replaced.Add(plan);
                afterReplace?.Invoke(replaced.Count);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var plan in replaced.AsEnumerable().Reverse())
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(plan.NewHash, SHA256.HashData(File.ReadAllBytes(plan.Target))))
                        throw new IOException("A switched shortcut changed; its original remains in the backup.");
                    File.Replace(plan.Backup, plan.Target, null, ignoreMetadataErrors: false);
                }
                catch (Exception rollback) { failures.Add(rollback); }
            if (failures.Count > 0) throw new AggregateException("Shortcut switch failed; some backups need manual recovery.", new[] { error }.Concat(failures));
            throw;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            foreach (var plan in plans) if (File.Exists(plan.Temporary)) File.Delete(plan.Temporary);
        }
    }
}
