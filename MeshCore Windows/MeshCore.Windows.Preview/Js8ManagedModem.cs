using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using MeshCore.Windows.Core;
using NAudio.CoreAudioApi;

namespace MeshCore.Windows.Preview;

internal sealed class Js8ManagedModem
{
    internal const string Profile = "MeshCore HFLink";
    private Process? _process;
    private bool _backgrounded;
    internal int Port { get; private set; }
    internal bool IsRunning => _process is { HasExited: false };

    internal void Start(string executable, Js8LinkSettings settings)
    {
        var station = settings.Station?.Validate() ?? throw new InvalidOperationException("Configure the HFLink station first.");
        using (var devices = new MMDeviceEnumerator())
        using (var input = devices.GetDevice(station.Input))
        using (var output = devices.GetDevice(station.Output))
        {
            if (input.State != DeviceState.Active || output.State != DeviceState.Active ||
                input.DataFlow != DataFlow.Capture || output.DataFlow != DataFlow.Render)
                throw new InvalidOperationException("Reconnect the selected radio audio devices or choose different devices in HFLink Settings.");
        }
        var running = Process.GetProcessesByName("JS8Call");
        try
        {
            if (running.Length != 0)
                throw new InvalidOperationException("Close the running JS8Call window before starting the HFLink modem.");
        }
        finally { foreach (var process in running) process.Dispose(); }
        _process?.Dispose();
        _process = null;
        _backgrounded = false;

        var profile = $"JS8Call - {Profile}";
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), profile);
        Directory.CreateDirectory(directory);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        WriteProfile(Path.Combine(directory, profile + ".ini"), settings, Port);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            WindowStyle = ProcessWindowStyle.Minimized
        };
        start.ArgumentList.Add("--rig-name");
        start.ArgumentList.Add(Profile);
        _process = Process.Start(start) ?? throw new IOException("Windows could not start the HFLink modem.");
    }

    internal static void WriteProfile(string path, Js8LinkSettings settings, int port)
    {
        settings = settings.Validate();
        var station = settings.Station ?? throw new InvalidOperationException("Configure the HFLink station first.");
        string ptt = "PTT_method_" + station.Ptt;
        string group = settings.PublicGroup == "@ALLCALL" ? "" : "@" + settings.PublicGroup;
        File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture, $"""
            [Configuration]
            MyCall={station.Callsign}
            MyGrid={station.Grid}
            MyGroups={group}
            Rig=None
            PTTMethod=@Variant(\0\0\0\x7f\0\0\0\x1eTransceiverFactory::PTTMethod\0\0\0\0\x{ptt.Length + 1:x}{ptt}\0)
            PTTport={station.SerialPort}
            TxDelay={station.TxDelay}
            SoundInId=@ByteArray({station.Input})
            SoundOutId=@ByteArray({station.Output})
            AudioInputChannel=Mono
            AudioOutputChannel=Mono
            TCPEnabled=true
            TCPServer=127.0.0.1
            TCPServerPort={port}
            AcceptTCPRequests=true
            UDPEnabled=false
            MonitorOFF=false
            AutoreplyOnAtStartup={station.AutoReply.ToString().ToLowerInvariant()}
            AutoreplyConfirmation=false
            PSKReporter=false
            SpotToAPRS=false

            [Common]
            SubMode={station.Speed}
            Freq={station.Offset}
            """));
    }

    internal void KeepInBackground()
    {
        if (_process is not { HasExited: false } process || _backgrounded) return;
        foreach (var (window, title) in WindowsFor(process.Id))
        {
            if (title.StartsWith($"JS8Call - {Profile} ", StringComparison.Ordinal))
            {
                ShowWindowAsync(window, 6);
                _backgrounded = true;
            }
            else if (title.StartsWith("Wide Graph", StringComparison.OrdinalIgnoreCase))
                ShowWindowAsync(window, 6);
        }
    }

    internal async Task StopAsync()
    {
        if (_process is not { } process) return;
        await CloseProcessAsync(process, Profile);
        process.Dispose();
        _process = null;
        _backgrounded = false;
    }

    internal static async Task CloseProcessAsync(Process process, string profile)
    {
        if (process.HasExited) return;
        IntPtr main = WindowsFor(process.Id).FirstOrDefault(item =>
            item.Title.StartsWith($"JS8Call - {profile} ", StringComparison.Ordinal)).Window;
        if (main == IntPtr.Zero || !PostMessage(main, 0x0010, IntPtr.Zero, IntPtr.Zero))
            throw new IOException("Close the HFLink modem window before restarting it.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (process.HasExited) return;
            if (IsWindow(main)) throw new IOException("The HFLink modem did not finish shutting down.");
            // JS8Call takes the rig offline in its close handler before destroying the main window.
            // Some Windows builds leave a headless process behind after that handler returns.
            process.Kill();
            await process.WaitForExitAsync();
        }
    }

    private static List<(IntPtr Window, string Title)> WindowsFor(int processId)
    {
        var windows = new List<(IntPtr, string)>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;
            var title = new StringBuilder(256);
            if (GetWindowText(window, title, title.Capacity) > 0) windows.Add((window, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int size);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
}
