namespace MeshCore.Windows.Core;

/// <summary>Parses the exact handoff used by the existing Python desktop updater.</summary>
public static class LegacyUpdateHandoff
{
    public static int Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 2 || args[0] != "--update" ||
            !int.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            throw new ArgumentException("Invalid legacy update handoff.", nameof(args));
        return pid;
    }
}
