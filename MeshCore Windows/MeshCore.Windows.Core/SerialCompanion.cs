using System.IO.Ports;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>A single companion connected using the standard framed USB serial protocol.</summary>
public sealed class SerialCompanion : ICompanionBoard
{
    private readonly Stream _stream;
    private readonly IDisposable _owner;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Task _reader;
    private int _closed, _disposed;
    public FrameQueue Received { get; } = new();
    public bool Connected => Volatile.Read(ref _closed) == 0;
    public Task Completion => _reader;

    private SerialCompanion(Stream stream, IDisposable owner)
    {
        _stream = stream;
        _owner = owner;
        _reader = ReadLoopAsync();
    }

    public static SerialCompanion Open(string portName)
    {
        if (portName is null || !Regex.IsMatch(portName, "^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Select a valid serial port.", nameof(portName));
        var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        { DtrEnable = false, RtsEnable = false, Handshake = Handshake.None };
        try { port.Open(); return new SerialCompanion(port.BaseStream, port); }
        catch { port.Dispose(); throw; }
    }

    /// <summary>Wraps an already opened duplex stream; useful for test and transport adapters.</summary>
    public static SerialCompanion FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite) throw new ArgumentException("A duplex stream is required.", nameof(stream));
        return new SerialCompanion(stream, stream);
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
        byte[] frame = CompanionFrames.EncodeOutgoing(payload.Span);
        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (!Connected) throw new IOException("The companion connection is closed.");
            try { await _stream.WriteAsync(frame, cancellationToken); await _stream.FlushAsync(cancellationToken); }
            catch { Close(); throw; }
        }
        finally { _writer.Release(); }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _owner.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Close();
        try { await _reader; } catch (IOException) { } catch (InvalidOperationException) { }
        _stop.Dispose();
    }
}
