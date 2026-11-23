using System.Text.RegularExpressions;
using MeshCore.Windows.Core;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;

namespace MeshCore.Windows.Preview;

/// <summary>Describes available serial ports while retaining a fallback if device metadata is unavailable.</summary>
internal static class SerialPortDescriptions
{
    internal sealed record Port(string port, string name, bool candidate, string detail);

    internal static async Task<Port[]> ListAsync()
    {
        string[] ports = UsbGps.Ports();
        var details = new Dictionary<string, Port>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var devices = await DeviceInformation.FindAllAsync(SerialDevice.GetDeviceSelector());
            foreach (var device in devices)
            {
                var match = Regex.Match(device.Name, @"\((COM[0-9]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                string port = match.Groups[1].Value.ToUpperInvariant();
                if (!ports.Contains(port, StringComparer.OrdinalIgnoreCase)) continue;
                bool candidate = device.Id.Contains("VID_10C4", StringComparison.OrdinalIgnoreCase) ||
                    device.Id.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase) || device.Id.Contains("VID_303A", StringComparison.OrdinalIgnoreCase);
                details[port] = new Port(port, device.Name, candidate, "");
            }
        }
        catch (Exception error)
        { System.Diagnostics.Debug.WriteLine("Serial port descriptions unavailable: " + error.GetType().Name); }
        return ports.Select(port => details.GetValueOrDefault(port) ?? new Port(port, port, false, ""))
            .OrderBy(port => !port.candidate).ThenBy(port => port.port, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
