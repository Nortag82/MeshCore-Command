using System.Runtime.InteropServices;

namespace MeshCore.Windows.Installer;

/// <summary>Creates a separate, opt-in Start menu shortcut without replacing existing files.</summary>
internal static class PreviewShortcut
{
    internal const string Name = "MeshCore Command Native Preview.lnk";
    internal const string Arguments = "--native-candidate";

    internal static string EnsureInstalled(string programsDirectory, string launcher)
    {
        programsDirectory = Path.GetFullPath(programsDirectory);
        if (!Directory.Exists(programsDirectory) || (File.GetAttributes(programsDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Start menu folder is unavailable or is a link.");
        string executable = Path.GetFullPath(launcher);
        foreach (string name in new[] { "MeshCore Command.lnk", "MeshCore Command (Windows).lnk" })
        {
            string path = Path.Combine(programsDirectory, name);
            if (!File.Exists(path)) return Create(programsDirectory, executable, name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && PointsTo(path, executable)) return path;
        }
        throw new IOException("Both MeshCore Command Start menu shortcut names are already in use.");
    }

    private static bool PointsTo(string path, string executable)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            return string.Equals((string)((dynamic)shortcut).TargetPath, executable, StringComparison.OrdinalIgnoreCase) &&
                (string)((dynamic)shortcut).Arguments == Arguments;
        }
        catch (COMException) { return false; }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static object CreateShell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut service is unavailable."))
        ?? throw new IOException("Windows shortcut service is unavailable.");

    internal static string Create(string programsDirectory, string launcher, string name = Name)
    {
        string directory = Path.GetFullPath(programsDirectory);
        string executable = Path.GetFullPath(launcher);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Start menu folder is unavailable or is a link.");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The MeshCore Command launcher is unavailable or is a link.");
        string target = Path.Combine(directory, name);
        if (File.Exists(target)) throw new IOException("A shortcut with this name already exists. It was not changed.");
        string temporary = Path.Combine(directory, ".mcc-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null, shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(temporary);
            ((dynamic)shortcut).TargetPath = executable;
            ((dynamic)shortcut).Arguments = Arguments;
            ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(executable)!;
            ((dynamic)shortcut).Description = "Open MeshCore Command";
            ((dynamic)shortcut).Save();
            File.Move(temporary, target, overwrite: false);
            return target;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
