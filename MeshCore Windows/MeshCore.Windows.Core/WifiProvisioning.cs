using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>USB Wi-Fi provisioning protocol; callers control the serial port and resets.</summary>
public sealed class WifiProvisioning : IDisposable
{
    private byte[]? _command;
    private bool _disposed;
    public bool Sent { get; private set; }
    public bool Saved { get; private set; }
    public IPAddress? Address { get; private set; }

    public WifiProvisioning(string ssid, string password)
    {
        FirmwareCatalog.ValidateWifiCredentials(ssid, password);
        byte[] ssidBytes = Encoding.UTF8.GetBytes(ssid), passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            _command = new byte[8 + ssidBytes.Length * 2 + 1 + passwordBytes.Length * 2 + 1];
            "MC_WIFI:"u8.CopyTo(_command); int offset = 8;
            static void Hex(byte[] source, byte[] destination, ref int offset)
            {
                const string digits = "0123456789abcdef";
                foreach (byte value in source) { destination[offset++] = (byte)digits[value >> 4]; destination[offset++] = (byte)digits[value & 15]; }
            }
            Hex(ssidBytes, _command, ref offset); _command[offset++] = (byte)':';
            Hex(passwordBytes, _command, ref offset); _command[offset] = (byte)'\n';
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(ssidBytes); System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    /// <summary>Returns a command only once when firmware signals readiness. Never log the return value.</summary>
    public byte[]? ProcessLine(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (line.Contains("MC_PROVISION_ERROR", StringComparison.Ordinal)) throw new IOException("The device rejected its Wi-Fi settings.");
        if (line.Contains("PC WiFi: no connection", StringComparison.Ordinal)) throw new IOException("Wi-Fi was saved but the radio could not join the network.");
        if (!Sent && line.Contains("MC_PROVISION_READY_V1", StringComparison.Ordinal))
        {
            Sent = true; byte[] command = _command!; _command = null; return command;
        }
        if (line.Contains("MC_PROVISION_OK", StringComparison.Ordinal)) Saved = true;
        var match = Regex.Match(line, @"PC WiFi: .* at (\d+\.\d+\.\d+\.\d+)");
        if (match.Success && IPAddress.TryParse(match.Groups[1].Value, out var address) && address.AddressFamily == AddressFamily.InterNetwork) Address = address;
        return null;
    }

    public bool Complete => Sent && Saved && Address is not null;

    public void Dispose() { if (_command is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_command); _command = null; _disposed = true; }
}
