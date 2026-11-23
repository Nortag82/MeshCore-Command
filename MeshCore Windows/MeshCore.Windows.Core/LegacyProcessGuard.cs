using System.ComponentModel;
using System.Diagnostics;

namespace MeshCore.Windows.Core;

/// <summary>Fails closed if another copy of the legacy executable is still running.</summary>
public static class LegacyProcessGuard
{
    public static void EnsureStopped(string expectedExecutable)
    {
        string expected = Path.GetFullPath(expectedExecutable);
        string name = Path.GetFileNameWithoutExtension(expected);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Invalid legacy executable path.");
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    string? actual = process.MainModule?.FileName;
                    if (actual is null) throw new IOException("Cannot verify another process with the legacy app's name.");
                    if (string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Another legacy app window is still running. Close it before updating.");
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception error) { throw new IOException("Cannot verify another process with the legacy app's name.", error); }
            }
        }
    }
}
