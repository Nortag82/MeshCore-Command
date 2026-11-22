using System.IO.Ports;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>Configure Wi-Fi after flashing; never returns or logs the supplied credentials.</summary>
public static class UsbWifiProvisioner
{
    public static async Task<IPAddress> ConfigureAsync(string portName, string ssid, string password, CancellationToken cancellationToken = default)
    {
        if (portName is null || !Regex.IsMatch(portName, "^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new ArgumentException("Select a valid COM port.", nameof(portName));
        using var state = new WifiProvisioning(ssid, password);
        using var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One) { DtrEnable = false, RtsEnable = false, Handshake = Handshake.None };
        port.Open();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(65));
        using var registration = timeout.Token.Register(() => { try { port.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { } });
        try
        {
            port.DtrEnable = false; port.RtsEnable = true;
            await Task.Delay(150, timeout.Token);
            port.RtsEnable = false;
            return await RunStreamAsync(port.BaseStream, state, timeout.Token);
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException)
        { throw new OperationCanceledException("Wi-Fi provisioning was cancelled.", error, cancellationToken); }
        catch (Exception error) when (timeout.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException)
        { throw new TimeoutException("Wi-Fi provisioning did not finish; check the board and network.", error); }
    }

    public static async Task<IPAddress> RunStreamAsync(Stream stream, WifiProvisioning state, CancellationToken cancellationToken = default)
    {
        byte[] one = new byte[1]; var line = new List<byte>(); bool overflow = false;
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0) throw new EndOfStreamException("USB connection closed during provisioning.");
            if (one[0] == (byte)'\n')
            {
                if (!overflow && line.All(b => b <= 127))
                {
                    byte[]? command = state.ProcessLine(Encoding.ASCII.GetString(line.ToArray()));
                    if (command is not null)
                    {
                        try { await stream.WriteAsync(command, cancellationToken); await stream.FlushAsync(cancellationToken); }
                        finally { CryptographicOperations.ZeroMemory(command); }
                    }
                    if (state.Complete) return state.Address!;
                }
                line.Clear(); overflow = false;
            }
            else if (line.Count < 1024) line.Add(one[0]);
            else overflow = true;
        }
    }
}
