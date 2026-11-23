using System.Runtime.InteropServices;

namespace MeshCore.Windows.Installer;

/// <summary>Creates a separate, opt-in Start menu shortcut without replacing existing files.</summary>
internal static class PreviewShortcut
{
    internal const string Name = "MeshCore Command Native Preview.lnk";
    internal const string Arguments = "--native-candidate";

    internal static string Create(string programsDirectory, string launcher)
    {
        string directory = Path.GetFullPath(programsDirectory);
        string executable = Path.GetFullPath(launcher);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Start menu folder is unavailable or is a link.");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The preview launcher is unavailable or is a link.");
        string target = Path.Combine(directory, Name);
        if (File.Exists(target)) throw new IOException("A shortcut with this name already exists. It was not changed.");
        string temporary = Path.Combine(directory, ".mcc-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut service is unavailable."));
            if (shell is null) throw new IOException("Windows shortcut service is unavailable.");
            shortcut = ((dynamic)shell).CreateShortcut(temporary);
            ((dynamic)shortcut).TargetPath = executable;
            ((dynamic)shortcut).Arguments = Arguments;
            ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(executable)!;
            ((dynamic)shortcut).Description = "Open the experimental MeshCore Command native candidate";
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
