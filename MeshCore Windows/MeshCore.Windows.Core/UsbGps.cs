using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public static class UsbGps
{
    private static readonly int[] Speeds = [4800, 9600, 19200, 38400, 57600, 115200];

    public static string[] Ports() => SerialPort.GetPortNames().OrderBy(port => port, StringComparer.OrdinalIgnoreCase).ToArray();

    public static async Task<GpsPosition> ReadAsync(string name, int baud, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (name is null || !Regex.IsMatch(name, "^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Select a valid GPS COM port.", nameof(name));
        if (!Speeds.Contains(baud)) throw new ArgumentOutOfRangeException(nameof(baud));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var port = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        { DtrEnable = false, RtsEnable = false, Handshake = Handshake.None };
        port.Open();
        port.DiscardInBuffer();
        // Closing the port also releases pending reads on Windows when the deadline expires.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var registration = deadline.Token.Register(() => { try { port.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { } });
        try { return await ReadFromStreamAsync(port.BaseStream, deadline.Token); }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException)
        { throw new OperationCanceledException("GPS read cancelled.", error, cancellationToken); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException)
        { throw new TimeoutException("No valid GPS fix received. Check the receiver, baud rate and sky view.", error); }
    }

    public static async Task<GpsPosition> ReadFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] one = new byte[1];
        var line = new List<byte>(256);
        bool overflow = false;
        while (true)
        {
            int count = await stream.ReadAsync(one, cancellationToken);
            if (count == 0) throw new EndOfStreamException("GPS stream closed before a fix was received.");
            if (one[0] == (byte)'\n')
            {
                if (!overflow && line.All(c => c <= 127))
                {
                    var fix = NmeaPosition.Parse(Encoding.ASCII.GetString(line.ToArray()));
                    if (fix is not null) return fix;
                }
                line.Clear(); overflow = false;
            }
            else if (line.Count < 256) line.Add(one[0]);
            else overflow = true;
        }
    }
}
