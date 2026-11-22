using System.Net;
using System.Net.Sockets;

namespace MeshCore.Windows.Core;

/// <summary>Reopens a Wi-Fi connection after failure without replaying writes.</summary>
public sealed class WifiBoard : ICompanionBoard
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly TimeSpan _initialDelay, _maxDelay;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Action<string>? _diagnostic;
    private WifiCompanion? _connection;
    private byte[]? _identity;
    private Task _loop = Task.CompletedTask;
    private int _disposed;

    public FrameQueue Received { get; } = new();
    public bool Connected { get { lock (_gate) return _connection is not null; } }
    private void Report(string message) { try { _diagnostic?.Invoke(message); } catch { /* Diagnostics cannot interrupt the connection. */ } }

    private WifiBoard(IPAddress address, int port, TimeSpan initialDelay, TimeSpan maxDelay, Action<string>? diagnostic)
    {
        _address = address; _port = port; _initialDelay = initialDelay; _maxDelay = maxDelay; _diagnostic = diagnostic;
    }

    internal void BindIdentity(byte[] identity)
    {
        if (identity.Length != 32 || identity.All(value => value == 0)) throw new ArgumentException("Invalid board identity.");
        lock (_gate) _identity = (byte[])identity.Clone();
    }

    public static async Task<WifiBoard> ConnectAsync(IPAddress address, int port = 5000, CancellationToken cancellationToken = default,
        TimeSpan? initialDelay = null, TimeSpan? maxDelay = null, Action<string>? diagnostic = null)
    {
        var board = new WifiBoard(address, port, initialDelay ?? TimeSpan.FromSeconds(1), maxDelay ?? TimeSpan.FromSeconds(30), diagnostic);
        if (board._initialDelay <= TimeSpan.Zero || board._maxDelay < board._initialDelay) throw new ArgumentOutOfRangeException(nameof(initialDelay));
        try
        {
            board._connection = await WifiCompanion.ConnectAsync(address, port, cancellationToken, board.Received);
            board._loop = board.RunAsync();
            return board;
        }
        catch { board._stop.Dispose(); throw; }
    }

    private async Task RunAsync()
    {
        var delay = _initialDelay;
        bool hasReconnected = false;
        long connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!_stop.IsCancellationRequested)
        {
            WifiCompanion? current;
            lock (_gate) current = _connection;
            if (current is not null)
            {
                try { await current.Completion; }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or OperationCanceledException) { Report("transport closed: " + ex.GetType().Name); }
                if (hasReconnected && System.Diagnostics.Stopwatch.GetElapsedTime(connectedAt) < TimeSpan.FromSeconds(30))
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxDelay.TotalMilliseconds));
                else delay = _initialDelay;
                lock (_gate) { if (ReferenceEquals(_connection, current)) _connection = null; }
                await current.DisposeAsync();
            }
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, _stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    byte[]? expected;
                    lock (_gate) expected = _identity;
                    var replacement = await WifiCompanion.ConnectAsync(_address, _port, timeout.Token, expected is null ? Received : null);
                    try
                    {
                        if (expected is not null)
                        {
                            byte[] actual = await BoardIdentity.ReadAsync(replacement, TimeSpan.FromSeconds(5), timeout.Token);
                            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("A different radio answered at the saved address.");
                            replacement.Received.ForwardTo(Received);
                        }
                    }
                    catch { await replacement.DisposeAsync(); throw; }
                    lock (_gate)
                    {
                        if (!_stop.IsCancellationRequested) { _connection = replacement; replacement = null!; }
                    }
                    if (replacement is not null) await replacement.DisposeAsync();
                    connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    hasReconnected = true;
                    Report("reconnected");
                    break;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or OperationCanceledException or TimeoutException or InvalidOperationException)
                { Report("reconnect failed: " + ex.GetType().Name); delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxDelay.TotalMilliseconds)); }
            }
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        WifiCompanion connection;
        lock (_gate) connection = _connection ?? throw new IOException("The companion is reconnecting.");
        return connection.SendAsync(payload, cancellationToken);
    }

    /// <summary>Closes the current transport so the normal identity-checked reconnect can run.</summary>
    public void RequestReconnect()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(WifiBoard));
        lock (_gate) _connection?.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        WifiCompanion? current;
        lock (_gate) current = _connection;
        current?.Stop();
        await _loop;
        _stop.Dispose();
    }
}
