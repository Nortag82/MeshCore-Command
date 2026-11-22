using System.Text;

namespace MeshCore.Windows.Core;

public static class BoardIdentity
{
    /// <summary>Identifies a board before its view opens while preserving unrelated frames.</summary>
    public static async Task<byte[]> ReadAsync(ICompanionBoard board, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        byte[] name = Encoding.ASCII.GetBytes("MeshCore Command");
        var request = new byte[8 + name.Length]; request[0] = 1; name.CopyTo(request, 8);
        await board.SendAsync(request, deadline.Token);
        try
        {
            while (true)
            {
                if (!board.Connected) throw new IOException("Board disconnected during identification.");
                byte[]? frame = board.Received.TakeFirstResponse(5, 36);
                if (frame is not null)
                {
                    byte[] identity = frame[4..36];
                    if (identity.All(b => b == 0)) throw new InvalidDataException("Board returned an invalid identity.");
                    return identity;
                }
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The board did not identify itself."); }
    }
}
