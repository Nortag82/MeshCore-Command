using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal sealed class BundledMercury(string dataDirectory) : IDisposable
{
    private static readonly string Payload = Path.Combine(AppContext.BaseDirectory, "mercury");
    private static readonly (string Name, string Sha256)[] PayloadFiles =
    [
        ("mercury.exe", "C571B805607509E1C0603B890844D834351FF21A7CF9930363D33DB6DEF1DD94"),
        ("libgcc_s_seh-1.dll", "172CFDC9D83703EC0A0CA2CEE08F6CF2A166B801717EE51A56B3A63B36BC2F83"),
        ("libhamlib-4.dll", "6677CD7F2C4702A7E8A4C45F360BDA3C6EF89E1E8A2A51ACE13EBDCEFE191DEA"),
        ("libusb-1.0.dll", "86D5D599C427D5C168BEA98F24584520BE6187DF142DD0FF1DEF4A834848A0A2"),
        ("libwinpthread-1.dll", "FC3CD567190F3F24E8201C2E1602F7D1F1E583A572EFB7DBD8746D7928538E94")
    ];
    private static readonly int[] Ports = [8300, 8301, 8100];
    private readonly string _directory = Path.Combine(dataDirectory, "mercury");
    private Process? _process;
    private SafeFileHandle? _job;
    private CancellationTokenSource? _monitor;
    public event Action<string>? ModemStopped;
    private string Runtime => Path.Combine(_directory, "runtime");
    private string Executable => Path.Combine(Runtime, "mercury.exe");
    public static bool Available => File.Exists(Path.Combine(Payload, "mercury.exe"));
    public bool Running => _process is { HasExited: false };
    public string ConfigPath => Path.Combine(_directory, "mercury.ini");

    public string PrepareSettings()
    {
        Directory.CreateDirectory(_directory);
        return ConfigPath;
    }

    public Task ConfigureFirewallAsync(CancellationToken stop)
    {
        StageRuntime();
        return MercuryFirewall.ConfigureAsync(Executable, stop);
    }

    public async Task EnsureReadyAsync(bool transmission, CancellationToken stop)
    {
        if (!File.Exists(ConfigPath)) throw new InvalidOperationException("Open Mercury settings and save your audio devices and PTT settings.");
        var config = MercurySettingsFile.Open(ConfigPath);
        if (config.Get("main", "input_device", "\0") == "\0" || config.Get("main", "output_device", "\0") == "\0")
            throw new InvalidOperationException("Choose both audio devices in Mercury settings before starting the bundled modem.");
        if (transmission && config.Get("ptt", "method", "none") is "none" or "")
            throw new InvalidOperationException("Set a push-to-talk method in Mercury settings before using live send or receive.");
        if (!await MercuryFirewall.IsProtectedAsync(Executable, stop))
        {
            Stop();
            throw new InvalidOperationException("Use Configure firewall before starting bundled Mercury.");
        }

        if (Running && OwnsListeners(_process!.Id)) return;
        Stop();
        StageRuntime();
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => Ports.Contains(endpoint.Port)))
            throw new IOException("A Mercury port is already in use. Close the external modem or select External Mercury in Station settings.");
        if (config.Get("main", "ui_enabled", "false") != "false")
        {
            config.Set("main", "ui_enabled", "false");
            config.Save();
        }

        var start = StartInfo(Executable, _directory, "-C", ConfigPath, "-p", "8300", "-b", "8100");
        _job = CreateStopOnCloseJob();
        try
        {
            stop.ThrowIfCancellationRequested();
            _process = Process.Start(start) ?? throw new IOException("Could not start bundled Mercury.");
            if (!AssignProcessToJobObject(_job, _process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (!OwnsListeners(_process.Id))
            {
                if (_process.HasExited) throw new IOException("Mercury closed. Check its audio and PTT settings.");
                await Task.Delay(200, deadline.Token);
            }
            _monitor = new CancellationTokenSource();
            _ = WatchAsync(_process, _monitor.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Stop();
            throw new IOException("Mercury did not open its control and data ports. Check its audio and PTT settings.");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private async Task WatchAsync(Process process, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(5_000, stop);
                bool ok = !process.HasExited && await MercuryFirewall.IsProtectedAsync(Executable, stop);
                if (stop.IsCancellationRequested || !ReferenceEquals(process, _process)) return;
                if (!ok)
                {
                    Stop();
                    ModemStopped?.Invoke("Mercury stopped. Check modem settings and firewall protection before restarting.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
        {
            if (!stop.IsCancellationRequested && ReferenceEquals(process, _process))
            {
                Stop();
                ModemStopped?.Invoke("Mercury stopped because Windows Firewall protection could not be verified.");
            }
        }
    }

    public void Stop()
    {
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
        _job?.Dispose();
        _job = null;
        if (_process is { } process)
        {
            _process = null;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) when (process.HasExited) { }
            finally { process.Dispose(); }
        }
    }

    public void Dispose() => Stop();

    private static ProcessStartInfo StartInfo(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = directory };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private void StageRuntime()
    {
        VerifyFiles(Payload);
        Directory.CreateDirectory(Runtime);
        foreach (var file in PayloadFiles)
        {
            string destination = Path.Combine(Runtime, file.Name);
            if (!Matches(destination, file.Sha256))
                File.Copy(Path.Combine(Payload, file.Name), destination, overwrite: true);
        }
        VerifyFiles(Runtime);
    }

    private static bool Matches(string path, string hash)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.Ordinal);
    }

    private static void VerifyFiles(string directory)
    {
        foreach (var file in PayloadFiles)
        {
            if (!Matches(Path.Combine(directory, file.Name), file.Sha256))
                throw new InvalidDataException("Bundled Mercury failed its file integrity check.");
        }
    }

    public static async Task VerifyPackageAsync()
    {
        VerifyFiles(Payload);
        using var process = Process.Start(StartInfo(Path.Combine(Payload, "mercury.exe"), Payload, "-V")) ?? throw new IOException("Bundled Mercury could not run.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        if (process.ExitCode != 0) throw new IOException("Bundled Mercury version check failed.");
    }

    // IPv4 MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owning process.
    private static bool OwnsListeners(int pid)
    {
        const int ipv4 = 2, listeners = 3, rowSize = 24;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv4, listeners, 0);
        if (size < 4) return false;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, ipv4, listeners, 0) != 0) return false;
            int count = Math.Min(Marshal.ReadInt32(buffer), (size - 4) / rowSize);
            var ports = new HashSet<int>();
            for (int row = 0; row < count; row++)
            {
                int offset = 4 + row * rowSize;
                if (Marshal.ReadInt32(buffer, offset + 20) != pid) continue;
                ports.Add((ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(buffer, offset + 8)));
            }
            return ports.Contains(8300) && ports.Contains(8301);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle CreateStopOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } };
        if (!job.IsInvalid && SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) return job;
        int error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool ordered, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
