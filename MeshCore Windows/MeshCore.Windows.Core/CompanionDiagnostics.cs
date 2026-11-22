using System.Buffers.Binary;

namespace MeshCore.Windows.Core;

public static class CompanionDiagnostics
{
    /// <summary>Read the companion battery voltage without consuming unrelated responses.</summary>
    public static async Task<int> ReadBatteryMillivoltsAsync(ICompanionBoard board, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        await board.SendAsync(new byte[] { 20 }, deadline.Token);
        try
        {
            while (true)
            {
                if (!board.Connected) throw new IOException("Board disconnected while reading battery voltage.");
                var frame = board.Received.TakeFirstResponse(12, 3);
                if (frame is not null) return BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("The board did not report its battery voltage."); }
    }
}
