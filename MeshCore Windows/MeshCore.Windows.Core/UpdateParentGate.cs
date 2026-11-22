using System.Diagnostics;

namespace MeshCore.Windows.Core;

/// <summary>Waits for the exact app process before an updater starts; never stops a process.</summary>
public static class UpdateParentGate
{
    public static async Task WaitAsync(int processId, long startUtcTicks, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || startUtcTicks <= 0 || timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processId));
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; } // The original app has already exited.
        using (process)
        {
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != startUtcTicks) throw new InvalidOperationException("The process ID belongs to a different process; update stopped.");
            }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("The app did not close; update stopped."); }
        }
    }
}
