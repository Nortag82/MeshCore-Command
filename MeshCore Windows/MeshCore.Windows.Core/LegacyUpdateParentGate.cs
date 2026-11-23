using System.ComponentModel;
using System.Diagnostics;

namespace MeshCore.Windows.Core;

/// <summary>Waits for the old updater's PID without ever stopping a process.</summary>
public static class LegacyUpdateParentGate
{
    public static async Task WaitAsync(int processId, string expectedExecutable, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processId));
        string expected = Path.GetFullPath(expectedExecutable);
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            string actual;
            try { actual = process.MainModule?.FileName ?? throw new IOException("Cannot verify the old app process."); }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            catch (Win32Exception error) { throw new IOException("Cannot verify the old app process; update stopped.", error); }
            if (!string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update PID belongs to a different program; update stopped.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("The old app did not close; update stopped."); }
        }
    }
}
