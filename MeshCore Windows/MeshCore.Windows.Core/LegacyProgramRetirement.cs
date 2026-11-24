namespace MeshCore.Windows.Core;

/// <summary>Removes the old program from its installed location while retaining a rollback copy.</summary>
public static class LegacyProgramRetirement
{
    public static string Retire(string localDirectory, string expectedExecutable)
    {
        string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localDirectory));
        string install = Path.Combine(local, "MeshCore Command");
        string executable = Path.Combine(install, "MeshCore Command.exe");
        if (!string.Equals(Path.GetFullPath(expectedExecutable), executable, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The old app is not installed in the expected location; it was not removed.");
        if (!Directory.Exists(local) || (File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0 ||
            !Directory.Exists(install) || (File.GetAttributes(install) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The old app's installation could not be verified; it was not removed.");
        LegacyProcessGuard.EnsureStopped(executable);
        string backup = Path.Combine(local, "MeshCore Command previous " + Guid.NewGuid().ToString("N"));
        Directory.Move(install, backup);
        return backup;
    }
}
