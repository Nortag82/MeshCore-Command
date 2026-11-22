namespace MeshCore.Windows.Core;

/// <summary>Windows BLE adapters provide one packet per notification and write.</summary>
public interface IBlePacketTransport : IAsyncDisposable
{
    event Action<byte[]>? PacketReceived;
    event Action? Disconnected;
    Task WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
}

public sealed class BleCompanion : ICompanionBoard
{
    public static readonly Guid ServiceId = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid WriteId = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    public static readonly Guid NotifyId = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    private readonly IBlePacketTransport _transport;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _closed, _disposed;
    public FrameQueue Received { get; } = new();
    public bool Connected => Volatile.Read(ref _closed) == 0;

    public BleCompanion(IBlePacketTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.PacketReceived += OnPacket;
        _transport.Disconnected += OnDisconnected;
    }

    private void OnPacket(byte[] packet)
    {
        if (!Connected) return;
        try { Received.Enqueue(packet); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { OnDisconnected(); }
    }

    private void OnDisconnected() => Interlocked.Exchange(ref _closed, 1);

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length is < 1 or > CompanionFrames.MaximumPayloadLength) throw new ArgumentOutOfRangeException(nameof(payload));
        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (!Connected) throw new IOException("Bluetooth connection is closed.");
            try { await _transport.WriteAsync(payload, cancellationToken); }
            catch { OnDisconnected(); throw; } // Delivery is uncertain; never retry automatically.
        }
        finally { _writer.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        OnDisconnected();
        _transport.PacketReceived -= OnPacket;
        _transport.Disconnected -= OnDisconnected;
        await _transport.DisposeAsync();
    }
}
