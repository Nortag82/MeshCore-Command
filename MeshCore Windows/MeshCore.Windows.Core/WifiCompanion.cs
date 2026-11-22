using System.Net;
using System.Net.Sockets;

namespace MeshCore.Windows.Core;

/// <summary>One Wi-Fi companion connection. The owner decides when to reconnect.</summary>
public sealed class WifiCompanion : ICompanionBoard
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Task _reader;
    private int _closed;

    public FrameQueue Received { get; }
    public bool Connected => Volatile.Read(ref _closed) == 0;
    public Task Completion => _reader;

    private WifiCompanion(TcpClient client, FrameQueue? received)
    {
        _client = client;
        _stream = client.GetStream();
        Received = received ?? new FrameQueue();
        _reader = ReadLoopAsync();
    }

    public static async Task<WifiCompanion> ConnectAsync(IPAddress address, int port = 5000, CancellationToken cancellationToken = default, FrameQueue? received = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("An IPv4 address is required.", nameof(address));
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(address, port, cancellationToken);
            return new WifiCompanion(client, received);
        }
        catch { client.Dispose(); throw; }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                Received.Enqueue(await CompanionFrames.ReadAsync(_stream, _stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        finally { Close(); }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var frame = CompanionFrames.EncodeOutgoing(payload.Span);
        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _closed) != 0) throw new IOException("The companion connection is closed.");
            try { await _stream.WriteAsync(frame, cancellationToken); }
            catch { Close(); throw; } // Never retry: the radio may already have received it.
        }
        finally { _writer.Release(); }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _client.Dispose();
    }

    internal void Stop() => Close();

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await _reader; } catch (IOException) { } catch (SocketException) { } catch (InvalidOperationException) { }
        _stop.Dispose();
    }
}
