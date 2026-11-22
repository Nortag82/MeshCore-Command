namespace MeshCore.Windows.Core;

/// <summary>Owns connected boards and rejects duplicate radio identities.</summary>
public sealed class BoardSessions : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (ICompanionBoard Board, string Identity)> _active = new(StringComparer.Ordinal);
    private bool _closed;

    public async Task AddAsync(string id, ICompanionBoard board, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        try { await _gate.WaitAsync(cancellationToken); }
        catch { await board.DisposeAsync(); throw; }
        try
        {
            if (_closed) throw new ObjectDisposedException(nameof(BoardSessions));
            if (_active.ContainsKey(id)) throw new InvalidOperationException("Board is already connected.");
            byte[] identityBytes = await BoardIdentity.ReadAsync(board, TimeSpan.FromSeconds(15), cancellationToken);
            string identity = Convert.ToHexString(identityBytes);
            if (_active.Values.Any(item => item.Identity == identity)) throw new InvalidOperationException("This radio is already connected through another board.");
            if (board is WifiBoard wifi) wifi.BindIdentity(identityBytes);
            _active.Add(id, (board, identity));
        }
        catch { await board.DisposeAsync(); throw; }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (_active.Remove(id, out var item)) await item.Board.DisposeAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_closed) return;
            _closed = true;
            foreach (var item in _active.Values) await item.Board.DisposeAsync();
            _active.Clear();
        }
        finally { _gate.Release(); }
    }
}
