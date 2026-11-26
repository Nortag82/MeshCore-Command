using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MeshCore.Windows.Core;
using MeshCore.Windows.Bluetooth;
using System.Net;
using System.Text.Json;
using System.Text;

namespace MeshCore.Windows.Preview;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (Environment.GetCommandLineArgs().Contains("--verify-mercury"))
        {
            try { BundledMercury.VerifyPackageAsync().GetAwaiter().GetResult(); }
            catch { Environment.ExitCode = 1; }
            return;
        }
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--audit-credential-conflicts"))
        {
            try
            {
                CredentialNamespaceMigration.EnsureNoConflicts(new WindowsBackupCredentials(), new WindowsBackupCredentials(native: true));
                Console.WriteLine("No conflicting native credentials were found; no credentials were changed.");
            }
            catch (Exception error) { Console.Error.WriteLine("Credential preflight failed: " + error.GetType().Name); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--serial-ports-test"))
        {
            try
            {
                string[] raw = UsbGps.Ports();
                var described = SerialPortDescriptions.ListAsync().GetAwaiter().GetResult();
                if (raw.Length != described.Length || raw.Any(port => !described.Any(item => item.port.Equals(port, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("Serial port descriptions omitted an available port.");
                Console.WriteLine($"Serial port enumeration succeeded ({described.Length} ports).");
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--wifi-reconnect-test"))
        {
            try
            {
                string address = args.FirstOrDefault(value => value.StartsWith("--ip=", StringComparison.Ordinal))?[5..] ?? throw new ArgumentException("Supply the test radio IP.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var board = WifiBoard.ConnectAsync(IPAddress.Parse(address), cancellationToken: deadline.Token).GetAwaiter().GetResult();
                var sessions = new BoardSessions();
                try
                {
                    sessions.AddAsync("hardware-test", board, deadline.Token).GetAwaiter().GetResult();
                    _ = CompanionDiagnostics.ReadBatteryMillivoltsAsync(board, TimeSpan.FromSeconds(10), deadline.Token).GetAwaiter().GetResult();
                    board.RequestReconnect();
                    while (board.Connected) Task.Delay(50, deadline.Token).GetAwaiter().GetResult();
                    while (!board.Connected) Task.Delay(100, deadline.Token).GetAwaiter().GetResult();
                    _ = CompanionDiagnostics.ReadBatteryMillivoltsAsync(board, TimeSpan.FromSeconds(10), deadline.Token).GetAwaiter().GetResult();
                    Console.WriteLine("Spare radio reconnected with the same identity and responded to a fresh battery request.");
                }
                finally { sessions.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            catch (Exception error) { Console.Error.WriteLine("Reconnect test failed: " + error.GetType().Name); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--verify-firmware"))
        {
            try { foreach (string target in FirmwareCatalog.SupportedTargets) _ = FirmwareCatalog.Verify(Path.Combine(AppContext.BaseDirectory, "firmware"), target); }
            catch (Exception error) { Console.Error.WriteLine("Firmware verification failed: " + error.GetType().Name); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--wifi-link-test"))
        {
            try
            {
                string address = args.FirstOrDefault(value => value.StartsWith("--ip=", StringComparison.Ordinal))?[5..] ?? throw new ArgumentException("Supply the test radio IP.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var board = WifiBoard.ConnectAsync(IPAddress.Parse(address), cancellationToken: deadline.Token).GetAwaiter().GetResult();
                try
                {
                    _ = BoardIdentity.ReadAsync(board, TimeSpan.FromSeconds(15), deadline.Token).GetAwaiter().GetResult();
                    int battery = CompanionDiagnostics.ReadBatteryMillivoltsAsync(board, TimeSpan.FromSeconds(10), deadline.Token).GetAwaiter().GetResult();
                    Console.WriteLine($"Wi-Fi identity exchange and battery request succeeded ({battery} mV).");
                }
                finally { board.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--flash-hardware-test"))
        {
            try { RunFlashHardwareTest(args).GetAwaiter().GetResult(); }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        string? testBoardName = args.FirstOrDefault(value => value.StartsWith("--ble-name=", StringComparison.Ordinal))?[11..];
        if (args.Contains("--ble-pair-test") || args.Contains("--ble-link-test"))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(testBoardName)) throw new ArgumentException("Specify the exact test board name with --ble-name.");
                var matches = WindowsBleTransport.ScanAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().Where(d => d.Name.Equals(testBoardName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new IOException("Expected one named test board; found " + matches.Length);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                if (args.Contains("--ble-pair-test"))
                {
                    Console.WriteLine("Waiting for PIN on standard input.");
                    var digits = new char[6];
                    try { for (int i = 0; i < digits.Length; i++) digits[i] = Console.ReadKey(intercept: true).KeyChar; }
                    catch (InvalidOperationException error) { throw new IOException("PIN input is unavailable.", error); }
                    string pin = new(digits); Array.Clear(digits);
                    WindowsBleTransport.PairAsync(matches[0].Address, pin, deadline.Token).GetAwaiter().GetResult();
                    Console.WriteLine("Authenticated pairing confirmed.");
                }
                else
                {
                    var transport = WindowsBleTransport.ConnectAsync(matches[0].Address, deadline.Token).GetAwaiter().GetResult();
                    var board = new BleCompanion(transport);
                    try
                    {
                        _ = BoardIdentity.ReadAsync(board, TimeSpan.FromSeconds(15), deadline.Token).GetAwaiter().GetResult();
                        int battery = CompanionDiagnostics.ReadBatteryMillivoltsAsync(board, TimeSpan.FromSeconds(10), deadline.Token).GetAwaiter().GetResult();
                        Console.WriteLine($"Bluetooth identity exchange and battery request succeeded ({battery} mV).");
                    }
                    finally { board.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                }
            }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + " 0x" + error.HResult.ToString("X8") + ": " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--ble-pairing-test"))
        {
            try
            {
                var devices = WindowsBleTransport.ScanAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(testBoardName)) throw new ArgumentException("Specify the exact test board name with --ble-name.");
                var matches = devices.Where(d => d.Name.Equals(testBoardName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new IOException("Expected one named test board; found " + matches.Length);
                var state = WindowsBleTransport.PairingStatusAsync(matches[0].Address).GetAwaiter().GetResult();
                Console.WriteLine($"Test board paired: {state.Paired}; authenticated: {state.Authenticated}");
            }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + " 0x" + error.HResult.ToString("X8") + ": " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--ble-diagnostic"))
        {
            try { var scan = WindowsBleTransport.DiagnoseScanAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult(); Console.WriteLine($"Advertisements: {scan.Advertisements}; MeshCore service: {scan.WithMeshCoreService}; matching device names: {string.Join(", ", scan.MatchingNames)}"); }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + " 0x" + error.HResult.ToString("X8") + ": " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--ble-scan-test"))
        {
            try { var devices = WindowsBleTransport.ScanAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult(); Console.WriteLine($"MeshCore BLE devices found: {devices.Count}"); Environment.ExitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + " 0x" + error.HResult.ToString("X8") + ": " + error.Message); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        string diagnosticsRoot = args.Contains("--native-candidate")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Candidate Data")
            : Path.Combine(Path.GetTempPath(), "MeshCoreCommandNativePreview");
        var diagnostics = new CrashLog(Path.Combine(diagnosticsRoot, "Logs", "crashes.log"));
        void Record(string category, Exception exception) { try { diagnostics.Record(category, exception); } catch { /* Logging must not cause another crash. */ } }
        Application.ThreadException += (_, eventArgs) => {
            Record("thread", eventArgs.Exception);
            MessageBox.Show("The native preview encountered an unexpected error and must close. A diagnostic entry was saved.", "MeshCore Command preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => { if (eventArgs.ExceptionObject is Exception error) Record("unhandled", error); };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => Record("task", eventArgs.Exception);
        if (args.Contains("--notification-test")) { Application.Run(new NotificationTest()); return; }
        if (args.Contains("--listener-retry-test"))
        {
            try
            {
                int attempts = 0;
                ProfileMigrationTest.StartListenerAsync(() => { if (++attempts < 3) throw new HttpListenerException(183); }, _ => Task.CompletedTask).GetAwaiter().GetResult();
                if (attempts != 3) throw new InvalidOperationException("Transient listener retry failed.");
                attempts = 0;
                try { ProfileMigrationTest.StartListenerAsync(() => { attempts++; throw new HttpListenerException(5); }, _ => Task.CompletedTask).GetAwaiter().GetResult(); throw new InvalidOperationException("Unexpected listener error was ignored."); }
                catch (HttpListenerException) { if (attempts != 1) throw new InvalidOperationException("Unexpected listener error was retried."); }
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--audit-legacy-profile")) { Application.Run(new LegacyProfileAudit()); return; }
        if (args.Contains("--audit-copied-profile"))
        {
            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--audit-root=", StringComparison.Ordinal))?[13..] ?? throw new ArgumentException("Missing audit root.")));
                string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("mcc-real-profile-audit-", StringComparison.Ordinal)) throw new ArgumentException("Invalid audit root.");
                NativeProfile profile = NativeProfileRegistry.Open(root);
                byte[] recordsDigest = CaptureProfileDigest(profile);
                Application.Run(new PreviewWindow(false, false, true, false, null, 0, profile.WebView, profile.Data, seedHistory: false, port: profile.Port, candidate: true, expectedRecords: recordsDigest));
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--audit-migrate-copied-profile"))
        {
            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--audit-root=", StringComparison.Ordinal))?[13..] ?? throw new ArgumentException("Missing audit root.")));
                string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("mcc-real-profile-audit-", StringComparison.Ordinal)) throw new ArgumentException("Invalid audit root.");
                NativeProfile source = NativeProfileRegistry.Open(root);
                string sourceWebView = Path.Combine(source.WebView, "EBWebView");
                string registry = Path.Combine(root, "candidate-copy-registry");
                using var lease = NativeInstanceLease.Acquire(registry);
                var sourceCredentials = new PreviewCredentials();
                string key = new('a', 64); sourceCredentials.Put("secure", key, "synthetic-audit-secret");
                using var startup = new NativeCandidateStartup(registry, sourceWebView, source.Data, port: source.Port, hidden: true, credentials: sourceCredentials);
                Application.Run(startup);
                NativeProfile migrated = startup.Profile ?? throw new InvalidDataException("Copied profile migration failed.");
                var credentials = new PreviewCredentials();
                Task.Run(() => NativeCredentialIsolation.EnsureWithLegacyLockAsync(registry, sourceWebView, sourceCredentials, credentials)).GetAwaiter().GetResult();
                if (credentials.Get("secure", key) != "synthetic-audit-secret") throw new InvalidDataException("Copied credential migration failed.");
                byte[] digest = CaptureProfileDigest(migrated);
                Application.Run(new PreviewWindow(false, false, true, false, null, 0, migrated.WebView, migrated.Data, seedHistory: false, port: migrated.Port, credentials: credentials, candidate: true, expectedRecords: digest));
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--candidate-sequence-test"))
        {
            try
            {
                string? rootArg = args.FirstOrDefault(value => value.StartsWith("--test-root=", StringComparison.Ordinal));
                string root = Path.GetFullPath(rootArg?[12..] ?? throw new ArgumentException("Missing test root."));
                string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("mcc-profile-integration-", StringComparison.Ordinal)) throw new ArgumentException("Invalid test root.");
                string registry = Path.Combine(root, args.Contains("--test-alter-record-digest") ? "sequence-mismatch-registry" : "sequence-registry");
                using var lease = NativeInstanceLease.Acquire(registry);
                string legacyProfile = Path.Combine(root, "original", "EBWebView");
                var sourceCredentials = new PreviewCredentials();
                string testKey = new('a', 64); sourceCredentials.Put("secure", testKey, "synthetic-sequence-secret");
                using var startup = new NativeCandidateStartup(registry, legacyProfile, Path.Combine(root, "data"), port: 19484, hidden: true, credentials: sourceCredentials);
                Application.Run(startup);
                NativeProfile profile = startup.Profile ?? throw new InvalidDataException("Candidate startup did not select a profile.");
                var isolatedCredentials = new PreviewCredentials();
                Task.Run(() => NativeCredentialIsolation.EnsureWithLegacyLockAsync(registry, legacyProfile, sourceCredentials, isolatedCredentials)).GetAwaiter().GetResult();
                if (isolatedCredentials.Get("secure", testKey) != "synthetic-sequence-secret") throw new InvalidDataException("Candidate did not isolate credentials.");
                byte[] recordsDigest = CaptureProfileDigest(profile);
                if (args.Contains("--test-alter-record-digest")) recordsDigest[0] ^= 1;
                Application.Run(new PreviewWindow(false, false, false, true, null, 0, profile.WebView, profile.Data, seedHistory: false, port: profile.Port, credentials: isolatedCredentials, candidate: true, expectedRecords: recordsDigest));
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--profile-integration-test")) { Application.Run(new ProfileMigrationTest()); return; }
        if (args.Contains("--native-candidate"))
        {
            string registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Candidate Data");
            try
            {
                using var lease = NativeInstanceLease.Acquire(registry);
                if (args.Contains("--upgrade-require-fresh")) NativeProfileRegistry.RequireUnselected(registry);
                var legacyCredentials = new WindowsBackupCredentials();
                using var startup = new NativeCandidateStartup(registry: registry, credentials: legacyCredentials);
                Application.Run(startup);
                if (startup.Profile is NativeProfile profile)
                {
                    var credentials = new WindowsBackupCredentials(native: true);
                    string legacyProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore WiFi Data", "WebView", "EBWebView");
                    Task.Run(() => NativeCredentialIsolation.EnsureWithLegacyLockAsync(registry, legacyProfile, legacyCredentials, credentials)).GetAwaiter().GetResult();
                    byte[]? recordsDigest = args.Any(a => a.StartsWith("--startup-receipt=", StringComparison.Ordinal)) ? CaptureProfileDigest(profile) : null;
                    Application.Run(new PreviewWindow(true, true, false, false, null, 0, profile.WebView, profile.Data,
                        seedHistory: false, port: profile.Port, credentials: credentials, candidate: true, expectedRecords: recordsDigest));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { MessageBox.Show("The native candidate cannot open this profile. Another native instance may be running.\n\n" + error.Message, "MeshCore Command", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
            return;
        }
        string? integrationIp = args.Contains("--integration-wifi-test") ? args.FirstOrDefault(value => value.StartsWith("--ip=", StringComparison.Ordinal))?[5..] : null;
        if (args.Contains("--integration-wifi-test") && integrationIp is null) { Console.Error.WriteLine("Supply the test radio IP."); Environment.ExitCode = 1; return; }
        if (integrationIp is not null)
            foreach (string name in new[] { "mcc-native-wifi-check.txt", "mcc-native-connection-observation.json", "mcc-preview-test-error.txt" })
                try { File.Delete(Path.Combine(Path.GetTempPath(), name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        int memorySeconds = int.TryParse(args.FirstOrDefault(value => value.StartsWith("--memory-sample-seconds=", StringComparison.Ordinal))?[24..], out int sample) ? Math.Clamp(sample, 0, 600) : 0;
        Application.Run(new PreviewWindow(args.Contains("--enable-experimental-radio"), args.Contains("--enable-experimental-flash"), args.Contains("--smoke-test"), args.Contains("--integration-test"), integrationIp, memorySeconds));
    }

    private static byte[] CaptureProfileDigest(NativeProfile profile)
    {
        using var audit = new CandidateProfileAudit(profile.WebView, profile.Port);
        Application.Run(audit);
        return audit.Digest ?? throw new IOException("The copied profile could not be verified before startup.", audit.Error);
    }

    private static async Task RunFlashHardwareTest(string[] args)
    {
        string Value(string prefix) => args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? throw new ArgumentException("Missing hardware test argument.");
        if (!Console.IsInputRedirected) throw new InvalidOperationException("Supply test network credentials using redirected standard input.");
        string port = Value("--port="), target = Value("--target="), backup = Value("--backup=");
        string ssid = Console.ReadLine() ?? throw new InvalidOperationException("Network name was not supplied.");
        string password = Console.ReadLine() ?? throw new InvalidOperationException("Network password was not supplied.");
        FirmwareCatalog.ValidateWifiCredentials(ssid, password);
        if (!File.Exists(backup) || new FileInfo(backup).Length != 8_388_608) throw new InvalidDataException("A complete 8 MB backup is required for this test.");
        string toolPath = Path.Combine(AppContext.BaseDirectory, "espflash.exe"), firmware = Path.Combine(AppContext.BaseDirectory, "firmware");
        var tool = new EspFlashTool(toolPath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await using var file = File.OpenRead(backup);
        string expected = Convert.ToHexString(await System.Security.Cryptography.MD5.HashDataAsync(file, deadline.Token));
        string device = await tool.RunAsync(new[] { "checksum-md5", "--port", port, "--chip", "esp32s3", "--non-interactive", "--skip-update-check", "--after", "no-reset", "0x0", "0x800000" }, TimeSpan.FromMinutes(2), deadline.Token);
        var match = System.Text.RegularExpressions.Regex.Match(device, @"(?im)^\s*0x([0-9a-f]{32})\s*$");
        if (!match.Success || !match.Groups[1].Value.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The backup does not match the selected board; no firmware was written.");
        Console.WriteLine("Verified backup matches the selected board.");
        var progress = new Progress<FlashProgress>(item => Console.WriteLine(item.Phase + ": " + item.Percent + "% — " + item.Message));
        await new EspFlashExecutor(tool).FlashAsync(firmware, target, port, UsbGps.Ports(), progress, deadline.Token);
        Console.WriteLine("Firmware verified; configuring Wi-Fi.");
        await Task.Delay(1000, deadline.Token);
        IPAddress address = await UsbWifiProvisioner.ConfigureAsync(port, ssid, password, deadline.Token);
        Console.WriteLine("Wi-Fi provisioning completed. Radio IP: " + address);
    }
}

/// <summary>Isolated UI shell used while the replacement is under development.</summary>
internal sealed class PreviewWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _warning = new() { Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleCenter,
        Text = "DEVELOPMENT PREVIEW — radio connections and data migration are disabled", BackColor = Color.DarkRed, ForeColor = Color.White };
    private BoardFrameEndpoint? _server;
    private readonly string _profile;
    private readonly bool _experimentalRadio;
    private readonly bool _experimentalFlash;
    private readonly bool _smokeTest;
    private readonly bool _integrationTest;
    private readonly string? _integrationIp;
    private readonly int _memorySeconds;
    private readonly int _port;
    private readonly string _testMessage = "native-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, ICompanionBoard> _boards = new(StringComparer.Ordinal);
    private readonly BoardSessions _sessions = new();
    private BoardProfiles _profiles;
    private BoardProfiles _serialProfiles;
    private readonly Dictionary<string, BoardProfile> _connectedProfiles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly OfflineMapStore _offlineMap = new();
    private readonly HttpClient _elevationHttp = new();
    private readonly NotifyIcon _tray = new();
    private readonly DesktopNotifications _notifications;
    private int _syntheticMessageNotification;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _connectionDiagnostics = new();
    private OfflineDownloads? _downloads;
    private bool _closing;
    private readonly ICredentialStore _credentials;
    private readonly string _dataDirectory;
    private readonly bool _seedHistory;
    private readonly bool _candidate;
    private readonly CandidateUpdater? _updater;
    private bool _updatePrompted;
    private byte[]? _pendingRestore;
    private FirmwareDialog? _firmwareDialog;
    private HfLinkView? _hfLinkView;
    private string? _hfLinkTestDirectory;
    private readonly string? _testReceipt, _testNonce;
    private readonly byte[]? _expectedRecords;
    private bool _startupReady;
    private bool _startupFailed;
    private bool _fullscreen;
    private FormBorderStyle _previousBorder;
    private FormWindowState _previousState;
    private Rectangle _previousBounds;

    public PreviewWindow(bool experimentalRadio, bool experimentalFlash, bool smokeTest, bool integrationTest, string? integrationIp, int memorySeconds,
        string? profileDirectory = null, string? dataDirectory = null, bool seedHistory = true, int port = 19484,
        ICredentialStore? credentials = null, bool candidate = false, byte[]? expectedRecords = null)
    {
        _profile = profileDirectory ?? Path.Combine(Path.GetTempPath(), "MeshCoreCommandNativePreview", "WebView");
        _dataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "MeshCoreCommandNativePreview", "Data");
        _seedHistory = seedHistory;
        _candidate = candidate;
        _expectedRecords = expectedRecords;
        _port = port;
        _credentials = credentials ?? new PreviewCredentials();
        _notifications = new DesktopNotifications(this, RestoreAndActivate);
        _updater = candidate ? CandidateUpdater.ForCurrentInstallation() : null;
        _experimentalRadio = experimentalRadio;
        _experimentalFlash = experimentalFlash;
        _integrationTest = integrationTest || integrationIp is not null;
        if (candidate || (integrationTest && integrationIp is null))
        {
            string[] args = Environment.GetCommandLineArgs();
            string prefix = candidate ? "--startup-receipt=" : "--test-startup-receipt=";
            string noncePrefix = candidate ? "--startup-nonce=" : "--test-startup-nonce=";
            string? path = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
            string? nonce = args.FirstOrDefault(a => a.StartsWith(noncePrefix, StringComparison.Ordinal))?[noncePrefix.Length..];
            if ((path is null) != (nonce is null)) throw new ArgumentException("Startup receipt and nonce are both required.");
            if (path is not null)
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
                string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(parent), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(parent).StartsWith("mcc-startup-test-", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid test startup receipt path.");
                _testReceipt = path; _testNonce = nonce;
                if (candidate && expectedRecords is null) throw new ArgumentException("A candidate startup receipt requires a profile verification digest.");
            }
        }
        _integrationIp = integrationIp;
        _memorySeconds = memorySeconds;
        _smokeTest = smokeTest || _integrationTest;
        _profiles = new BoardProfiles(Path.Combine(_dataDirectory, "boards.json"));
        _serialProfiles = new BoardProfiles(Path.Combine(_dataDirectory, "serial-boards.json"), allowSerial: true);
        if (experimentalRadio) _warning.Text = "DEVELOPMENT PREVIEW — experimental radio access enabled; your production data is not loaded";
        if (candidate) _warning.Text = "EARLY NATIVE CANDIDATE — separate profile and credentials; changes are not merged";
        Text = "MeshCore Command — native development preview";
        Width = 1280; Height = 800; MinimumSize = new Size(800, 500);
        if (_smokeTest) { Opacity = 0; ShowInTaskbar = false; }
        _content.Controls.Add(_web);
        Controls.Add(_content);
        if (_updater is null) Controls.Add(_warning);
        Shown += async (_, _) => await InitialiseAsync();
        FormClosing += async (_, e) => {
            if (_closing) return;
            if (_firmwareDialog?.IsBusy == true) { e.Cancel = true; MessageBox.Show(this, "Wait for firmware flashing and Wi-Fi setup to finish before closing.", "Firmware operation in progress"); return; }
            e.Cancel = true; _closing = true;
            try
            {
                _notifications.Dispose(); _tray.Visible = false; _tray.Dispose();
                _updater?.Dispose();
                if (_hfLinkView is not null) await _hfLinkView.StopAsync();
                if (_hfLinkTestDirectory is not null)
                {
                    foreach (string file in Directory.GetFiles(_hfLinkTestDirectory)) File.Delete(file);
                    Directory.Delete(_hfLinkTestDirectory);
                }
                if (_server is not null) await _server.DisposeAsync();
                if (_downloads is not null) await _downloads.DisposeAsync();
                await _sessions.DisposeAsync(); _elevationHttp.Dispose();
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine("Preview shutdown: " + error.Message); }
            finally { Close(); }
        };
    }

    private async Task InitialiseAsync()
    {
        try
        {
            byte[] shell = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "multi_board_shell.html"));
            string radio = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "radio.html"));
            string mapConfig = Path.Combine(_dataDirectory, "offline-map.json");
            if (File.Exists(mapConfig))
            {
                try { using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(mapConfig)); _offlineMap.Select(saved.RootElement.GetProperty("path").GetString()!); }
                catch (Exception error) when (error is IOException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException) { }
            }
            // The preview uses an isolated port; a migrated candidate keeps its profile's original origin.
            _elevationHttp.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCoreCommandNativePreview/1.0");
            _tray.Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "meshcore-command.ico"));
            _tray.Text = "MeshCore Command preview"; _tray.Visible = !_smokeTest;
            _tray.DoubleClick += (_, _) => RestoreAndActivate();
            _downloads = new OfflineDownloads(_elevationHttp, Path.Combine(_dataDirectory, "offline-maps"), _offlineMap, path => {
                Directory.CreateDirectory(_dataDirectory);
                WriteAtomicAsync(Path.Combine(_dataDirectory, "offline-map.json"), JsonSerializer.SerializeToUtf8Bytes(new { path })).GetAwaiter().GetResult();
            });
            _server = new BoardFrameEndpoint(_port, shell, radio, _credentials, _offlineMap, new ElevationService(_elevationHttp), (title, body) => {
                if (_integrationTest && _integrationIp is null && title == "MeshCore Command" &&
                    body == "New channel message. Open MeshCore Command to read it.")
                    Interlocked.Exchange(ref _syntheticMessageNotification, 1);
                if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _notifications.Show(title, body));
            }, _downloads);
            if (_integrationIp is not null)
            {
                var ip = IPAddress.Parse(_integrationIp);
                await ConnectAsync("wifi", ip.ToString(), async () => await WifiBoard.ConnectAsync(ip, diagnostic: message => _connectionDiagnostics.Enqueue(message)));
            }
            else if (_integrationTest)
            {
                var synthetic = _profiles.List().FirstOrDefault(p => p.Kind == "wifi" && p.Address == "127.0.0.1") ?? _profiles.Add("wifi", "127.0.0.1", "Synthetic test radio");
                var board = new SyntheticBoard(_testMessage, _expectedRecords is null ? null : () => _startupReady); _boards.Add(synthetic.Id, board); _connectedProfiles.Add(synthetic.Id, synthetic); _server.AddBoard(synthetic.Id, board);
            }
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            await _web.EnsureCoreWebView2Async(environment);
            if (_candidate) await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__MCC_NATIVE_CANDIDATE=true;");
            else await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__MCC_NATIVE_TEMPORARY=true;");
            if (_updater is not null) await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__MCC_NATIVE_UPDATE=true;window.__MCC_NATIVE_VERSION=" + JsonSerializer.Serialize(_updater.CurrentVersion) + ";");
            if (_integrationTest && _integrationIp is null && _seedHistory)
            {
                string key = "rcc6-history-" + string.Concat(Enumerable.Repeat("07", 32));
                string history = JsonSerializer.Serialize(new[] { new { id = "staged-history-test", key = "ch:0", text = "synthetic staged history", timestamp = 1, direction = "in", status = "received" } });
                await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("if(window.top===window)localStorage.setItem(" + JsonSerializer.Serialize(key) + "," + JsonSerializer.Serialize(history) + ");");
            }
            _web.CoreWebView2.NavigationStarting += (_, e) => {
                if (!e.Uri.StartsWith(_server.Origin + "/", StringComparison.Ordinal)) e.Cancel = true;
            };
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                if (window.top === window) {
                  let next=0;const pending=new Map();
                  window.chrome.webview.addEventListener('message',e=>{const d=e.data;if(d?.type==='result'&&pending.has(d.id)){pending.get(d.id)(d.value);pending.delete(d.id)}if(d?.type==='scan-progress')document.getElementById('native-scan-status').textContent=d.waiting?'Waiting for replies…':`Scanning ${d.attempted} / ${d.total}`});
                  const call=(method,...args)=>new Promise(resolve=>{const id=++next;pending.set(id,resolve);window.chrome.webview.postMessage({id,method,args})});
                  window.pywebview = {api: {
                    board_list: (...a)=>call('board_list',...a),
                    board_connect_wifi: (...a)=>call('board_connect_wifi',...a), board_connect_ble: (...a)=>call('board_connect_ble',...a), board_scan_ble: (...a)=>call('board_scan_ble',...a),
                    board_connect_serial: (...a)=>call('board_connect_serial',...a),
                    open_firmware: (...a)=>call('open_firmware',...a),
                    open_hflink: (...a)=>call('open_hflink',...a),
                    default_network: (...a)=>call('default_network',...a), wifi_scan: (...a)=>call('wifi_scan',...a),
                    board_rename: (...a)=>call('board_rename',...a), board_disconnect: (...a)=>call('board_disconnect',...a), board_forget: (...a)=>call('board_forget',...a),
                    choose_offline_map: (...a)=>call('choose_offline_map',...a), save_terrain_plan: (...a)=>call('save_terrain_plan',...a), open_terrain_plan: (...a)=>call('open_terrain_plan',...a),
                    ports: (...a)=>call('ports',...a), gps_read: (...a)=>call('gps_read',...a),
                    export_full_backup: (...a)=>call('export_full_backup',...a), prepare_full_restore: (...a)=>call('prepare_full_restore',...a),
                    commit_full_restore: (...a)=>call('commit_full_restore',...a), cancel_full_restore: (...a)=>call('cancel_full_restore',...a),
                    open_radio_backup: (...a)=>call('open_radio_backup',...a), save_radio_recovery: (...a)=>call('save_radio_recovery',...a), save_pc_recovery: (...a)=>call('save_pc_recovery',...a),
                    minimize: (...a)=>call('minimize',...a), toggle_fullscreen: (...a)=>call('toggle_fullscreen',...a), exit_app: (...a)=>call('close',...a)
                  }};
                  if(window.__MCC_NATIVE_UPDATE){window.pywebview.api.check_app_update=(...a)=>call('check_app_update',...a);window.pywebview.api.install_app_update=(...a)=>call('install_app_update',...a)}
                  if(window.__MCC_NATIVE_CANDIDATE)window.pywebview.api.restore_local_credentials=(...a)=>call('restore_local_credentials',...a);
                  window.addEventListener('DOMContentLoaded',()=>{
                    const wifi=document.getElementById('wifi'),ble=document.getElementById('ble'),row=document.getElementById('wifi-mode').parentElement;
                    const saved=document.createElement('div');saved.innerHTML='<label>Saved Wi-Fi boards<select id="native-saved-wifi"><option value="">Choose a saved board</option></select></label><div class="row"><button type="button" id="native-connect-saved-wifi">Connect</button><button type="button" id="native-forget-wifi">Remove saved board</button></div>';wifi.prepend(saved);
                    async function refreshSaved(){const result=await window.pywebview.api.board_list(),select=document.getElementById('native-saved-wifi');select.replaceChildren(new Option('Choose a saved board',''));for(const board of result.boards||[])if(board.kind==='wifi'){const option=new Option(`${board.name} — ${board.address}`,board.id);option.dataset.address=board.address;select.add(option)}}
                    document.getElementById('add').addEventListener('click',refreshSaved);
                    document.getElementById('native-saved-wifi').onchange=e=>{document.getElementById('wifi-address').value=e.target.selectedOptions[0]?.dataset.address||''};
                    document.getElementById('native-connect-saved-wifi').onclick=()=>{const select=document.getElementById('native-saved-wifi'),option=select.selectedOptions[0];if(!select.value||!option?.dataset.address){document.getElementById('status').textContent='Choose a saved Wi-Fi board first.';return}document.getElementById('wifi-address').value=option.dataset.address;document.getElementById('wifi-connect').click()};
                    document.getElementById('native-forget-wifi').onclick=async()=>{const select=document.getElementById('native-saved-wifi'),id=select.value;if(!id){document.getElementById('status').textContent='Choose a saved Wi-Fi board first.';return}const label=select.selectedOptions[0]?.textContent||'this board';if(!confirm(`Remove ${label} from saved boards on this PC? This does not reset the radio.`))return;if(select.value!==id){document.getElementById('status').textContent='Board selection changed; nothing was removed.';return}const result=await window.pywebview.api.board_forget(id);if(result.error){document.getElementById('status').textContent=result.error;return}await refreshSaved();document.getElementById('wifi-address').value='';document.getElementById('status').textContent='Saved board removed.'};
                    const flash=document.createElement('button');flash.type='button';flash.textContent='Flash firmware';document.getElementById('add').after(flash);
                    flash.onclick=async()=>{const result=await window.pywebview.api.open_firmware();if(result.error)alert(result.error)};
                    const hf=document.createElement('button');hf.id='open-hflink';hf.type='button';hf.textContent='HFLink';flash.after(hf);
                    hf.onclick=async()=>{const result=await window.pywebview.api.open_hflink();if(result.error)alert(result.error)};
                    const scan=document.createElement('details');scan.innerHTML='<summary>Find a radio on Wi-Fi</summary><label>Subnet<input id="native-subnet" placeholder="192.168.1.0/24"></label><button type="button" id="native-wifi-scan">Scan network</button><span id="native-scan-status" role="status"></span><label>Discovered radios<select id="native-scan-results"><option value="">Scan for a radio</option></select></label>';wifi.append(scan);
                    scan.addEventListener('toggle',async()=>{if(scan.open&&!document.getElementById('native-subnet').value){const info=await window.pywebview.api.default_network();document.getElementById('native-subnet').value=info.network||''}});
                    document.getElementById('native-scan-results').onchange=e=>{if(e.target.value)document.getElementById('wifi-address').value=e.target.value};
                    document.getElementById('native-wifi-scan').onclick=async()=>{const button=document.getElementById('native-wifi-scan');button.disabled=true;try{const result=await window.pywebview.api.wifi_scan(document.getElementById('native-subnet').value),select=document.getElementById('native-scan-results');select.replaceChildren(new Option(result.error||`${result.devices.length} radios found`,''));for(const item of result.devices||[])select.add(new Option(item.ip,item.ip));document.getElementById('native-scan-status').textContent=result.error||'Scan complete.'}finally{button.disabled=false}};
                    const mode=document.createElement('button');mode.type='button';mode.textContent='USB';row.append(mode);
                    const panel=document.createElement('div');panel.hidden=true;panel.innerHTML='<label>USB serial port<select id="native-usb-port"></select></label><button type="button" id="native-usb-refresh">Refresh ports</button> <button type="button" id="native-usb-connect">Connect USB board</button>';
                    ble.after(panel);
                    document.getElementById('wifi-mode').addEventListener('click',()=>panel.hidden=true);
                    document.getElementById('ble-mode').addEventListener('click',()=>panel.hidden=true);
                    async function refreshPorts(){const items=await window.pywebview.api.ports(),select=document.getElementById('native-usb-port');select.replaceChildren(new Option(items.length?'Choose a port':'No serial ports found',''));for(const item of items)select.add(new Option(item.name,item.port))}
                    mode.onclick=async()=>{wifi.hidden=true;ble.hidden=true;panel.hidden=false;await refreshPorts()};
                    document.getElementById('native-usb-refresh').onclick=refreshPorts;
                    document.getElementById('native-usb-connect').onclick=async()=>{const button=document.getElementById('native-usb-connect');button.disabled=true;try{const result=await window.pywebview.api.board_connect_serial(document.getElementById('native-usb-port').value);if(result.error){document.getElementById('status').textContent=result.error;return}document.getElementById('connect').close();window.dispatchEvent(new Event('pywebviewready'))}finally{button.disabled=false}};
                    window.dispatchEvent(new Event('pywebviewready'));
                  });
                }
                """);
            _web.CoreWebView2.WebMessageReceived += async (_, e) => {
                if (e.Source != _server.Origin + "/") return;
                await HandleRequestAsync(e.WebMessageAsJson);
            };
            _web.CoreWebView2.NavigationCompleted += async (_, e) => {
                if (!e.IsSuccess || _startupReady) return;
                try
                {
                    for (int attempt = 0; attempt < 100 && !_closing && !IsDisposed; attempt++)
                    {
                        string ready = await _web.CoreWebView2.ExecuteScriptAsync("document.getElementById('add') !== null && !!window.pywebview?.api?.board_list");
                        if (ready == "true")
                        {
                            if (_expectedRecords is not null)
                            {
                                byte[] actual = CandidateProfileAudit.Hash(await _web.CoreWebView2.ExecuteScriptAsync(CandidateProfileAudit.CaptureScript));
                                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(_expectedRecords, actual))
                                    throw new IOException("Copied profile records changed while starting the candidate.");
                            }
                            _startupReady = true;
                            if (_candidate && _testReceipt is not null) await StartupHealthGate.WriteAsync(_testReceipt, _testNonce!);
                            break;
                        }
                        await Task.Delay(100);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                {
                    System.Diagnostics.Debug.WriteLine("Startup readiness check failed: " + error.GetType().Name);
                    if (_expectedRecords is not null)
                    {
                        _startupFailed = true; Environment.ExitCode = 1;
                        if (!IsDisposed && !_closing) BeginInvoke(Close);
                    }
                }
            };
            if (_smokeTest)
                _web.CoreWebView2.NavigationCompleted += async (_, e) => {
                    if (!e.IsSuccess) Environment.ExitCode = 1;
                    else
                    {
                        if (_integrationTest)
                        {
                            Environment.ExitCode = 1;
                            for (int i = 0; i < (_integrationIp is null ? 50 : 150); i++)
                            {
                                await Task.Delay(200);
                                string expression = "!!document.getElementById('native-usb-connect') && !!document.querySelector('iframe')?.contentDocument?.getElementById('message-input') && !!document.querySelector('iframe')?.contentDocument?.getElementById('situation-map')";
                                if (_integrationIp is not null) expression += " && document.querySelector('iframe')?.contentDocument?.getElementById('link-label')?.textContent === 'Connected' && document.querySelector('iframe')?.contentDocument?.getElementById('node-id')?.textContent?.startsWith('ID ')";
                                else expression += " && document.querySelector('iframe')?.contentDocument?.getElementById('node-id')?.textContent?.startsWith('ID ') && !document.querySelector('iframe')?.contentDocument?.getElementById('nav-unread')?.hidden";
                                string state = await _web.CoreWebView2.ExecuteScriptAsync(expression);
                                if (state == "true") { Environment.ExitCode = 0; break; }
                            }
                            if (Environment.ExitCode == 0)
                            {
                                string version = JsonSerializer.Serialize(typeof(PreviewWindow).Assembly.GetName().Version?.ToString(3));
                                string branding = await _web.CoreWebView2.ExecuteScriptAsync("(()=>{const d=document.querySelector('iframe')?.contentDocument;return !!d?.querySelector('.brand')?.textContent.includes('MeshCore Command') && !!d?.getElementById('desktop-build')?.textContent.includes(" + version + ")})()");
                                if (branding != "true") Environment.ExitCode = 1;
                            }
                            if (_integrationIp is null && Environment.ExitCode == 0)
                            {
                                const string openHfLink = "document.getElementById('open-hflink').click()";
                                await _web.CoreWebView2.ExecuteScriptAsync("window.hfLinkRadioFrame = document.querySelector('iframe'); " + openHfLink);
                                for (int attempt = 0; attempt < 50 && _hfLinkView?.Visible != true; attempt++) await Task.Delay(100);
                                var view = _hfLinkView;
                                if (view is null || !view.Visible || view.FindForm() != this || _web.Visible || !CheckHfLinkButtonContrast(view) ||
                                    view.Controls.Find("hfLinkBack", true).SingleOrDefault() is not Button back)
                                    Environment.ExitCode = 1;
                                else
                                {
                                    back.PerformClick();
                                    if (!_web.Visible || view.Visible || await _web.CoreWebView2.ExecuteScriptAsync(
                                        "window.hfLinkRadioFrame === document.querySelector('iframe')") != "true") Environment.ExitCode = 1;
                                    if (Environment.ExitCode == 0)
                                    {
                                        await _web.CoreWebView2.ExecuteScriptAsync(openHfLink);
                                        for (int attempt = 0; attempt < 50 && !view.Visible; attempt++) await Task.Delay(100);
                                        if (_hfLinkView != view || !view.Visible || !await CheckHfLinkReceiveAsync(view, back))
                                            Environment.ExitCode = 1;
                                        await ReturnToRadioAsync();
                                    }
                                }
                            }
                            if (Environment.ExitCode != 0 && _integrationIp is not null)
                            {
                                string checks = await _web.CoreWebView2.ExecuteScriptAsync("(()=>{const f=document.querySelector('iframe'),d=f?.contentDocument;return JSON.stringify({usb:!!document.getElementById('native-usb-connect'),frame:!!d,message:!!d?.getElementById('message-input'),map:!!d?.getElementById('situation-map'),connected:d?.getElementById('link-label')?.textContent === 'Connected',identity:!!d?.getElementById('node-id')?.textContent?.startsWith('ID '),waiting:d?.getElementById('node-id')?.textContent?.startsWith('Waiting'),hidden:d?.hidden,active:f?.contentWindow?.__MCC_BOARD_ACTIVE,home:!!d?.querySelector('[data-view=home].active')})})()");
                                File.WriteAllText(Path.Combine(Path.GetTempPath(), "mcc-native-wifi-check.txt"), checks);
                            }
                            if (_integrationIp is null && Environment.ExitCode == 0)
                            {
                                await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.querySelector('button[data-view=messages]').click();document.querySelector('iframe').contentDocument.getElementById('messages-channels').click();");
                                string expected = JsonSerializer.Serialize(_testMessage);
                                Environment.ExitCode = 1;
                                for (int i = 0; i < 50; i++)
                                {
                                    string found = await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.getElementById('message-list').textContent.includes(" + expected + ") && document.querySelector('iframe').contentDocument.getElementById('message-list').textContent.includes('synthetic staged history')");
                                    if (found == "true") { Environment.ExitCode = 0; break; }
                                    await Task.Delay(200);
                                }
                            }
                            if (_integrationIp is null && !_seedHistory && Environment.ExitCode == 0)
                            {
                                string preserved = await _web.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('mcc-profile-secure-test')==='synthetic-secret-reference' && localStorage.getItem('mcc-profile-map-test')==='synthetic-map'");
                                if (preserved != "true") Environment.ExitCode = 1;
                            }
                            if (_integrationIp is null && Environment.ExitCode == 0)
                            {
                                for (int i = 0; i < 50 && Volatile.Read(ref _syntheticMessageNotification) == 0; i++)
                                    await Task.Delay(100);
                                if (Volatile.Read(ref _syntheticMessageNotification) == 0) Environment.ExitCode = 1;
                            }
                            if (_integrationIp is null && Environment.ExitCode == 0)
                            {
                                WindowState = FormWindowState.Normal;
                                Rectangle originalBounds = Bounds;
                                FormBorderStyle originalBorder = FormBorderStyle;
                                await _web.CoreWebView2.ExecuteScriptAsync("window.pywebview.api.toggle_fullscreen();");
                                for (int i = 0; i < 50 && !_fullscreen; i++) await Task.Delay(100);
                                if (!_fullscreen || FormBorderStyle != FormBorderStyle.None || Bounds != Screen.FromControl(this).Bounds)
                                    Environment.ExitCode = 1;
                                await _web.CoreWebView2.ExecuteScriptAsync("window.pywebview.api.toggle_fullscreen();");
                                for (int i = 0; i < 50 && _fullscreen; i++) await Task.Delay(100);
                                if (_fullscreen || FormBorderStyle != originalBorder || WindowState != FormWindowState.Normal || Bounds != originalBounds)
                                    Environment.ExitCode = 1;
                            }
                            if (Environment.ExitCode == 0)
                            {
                                WindowState = FormWindowState.Minimized;
                                await Task.Delay(500);
                                WindowState = FormWindowState.Normal;
                                Environment.ExitCode = 1;
                                for (int i = 0; i < 50; i++)
                                {
                                    string connected = await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('iframe')?.contentDocument?.getElementById('link-label')?.textContent === 'Connected'");
                                    if (connected == "true") { Environment.ExitCode = 0; break; }
                                    await Task.Delay(100);
                                }
                            }
                            if (_integrationIp is null && Environment.ExitCode == 0)
                            {
                                await _web.CoreWebView2.ExecuteScriptAsync("window.__scanOrder=[];window.pywebview.api.wifi_scan('127.0.0.1/32').then(()=>window.__scanOrder.push('scan'));window.pywebview.api.board_list().then(()=>window.__scanOrder.push('list'));");
                                Environment.ExitCode = 1;
                                for (int i = 0; i < 70; i++)
                                {
                                    string order = await _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__scanOrder)");
                                    if (order.Contains("scan") && order.Contains("list"))
                                    { Environment.ExitCode = order.IndexOf("list", StringComparison.Ordinal) < order.IndexOf("scan", StringComparison.Ordinal) ? 0 : 1; break; }
                                    await Task.Delay(100);
                                }
                            }
                        }
                        else
                        {
                            string result = await _web.CoreWebView2.ExecuteScriptAsync("document.getElementById('add') !== null && !!window.pywebview?.api?.board_list");
                            Environment.ExitCode = result == "true" ? 0 : 1;
                        }
                    }
                    if (_startupFailed) Environment.ExitCode = 1;
                    if (Environment.ExitCode == 0 && _expectedRecords is not null && _testReceipt is null)
                    {
                        for (int attempt = 0; attempt < 100 && !_startupReady; attempt++) await Task.Delay(100);
                        if (!_startupReady) Environment.ExitCode = 1;
                    }
                    if (Environment.ExitCode == 0 && _testReceipt is not null && !_candidate)
                    {
                        try
                        {
                            for (int attempt = 0; attempt < 100 && !_startupReady; attempt++) await Task.Delay(100);
                            if (!_startupReady) throw new IOException("Native shell did not confirm readiness.");
                            await StartupHealthGate.WriteAsync(_testReceipt, _testNonce!);
                        }
                        catch { Environment.ExitCode = 1; }
                    }
                    if (Environment.ExitCode == 0 && _memorySeconds > 0)
                    {
                        var timer = System.Diagnostics.Stopwatch.StartNew(); int disconnectedSamples = 0; var interruptions = new List<object>();
                        while (timer.Elapsed < TimeSpan.FromSeconds(_memorySeconds))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2));
                            string connected = await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('iframe')?.contentDocument?.getElementById('link-label')?.textContent === 'Connected'");
                            if (connected != "true") {
                                disconnectedSamples++;
                                string label = await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('iframe')?.contentDocument?.getElementById('link-label')?.textContent");
                                string status = label is "\"Reconnecting\"" or "\"Offline\"" or "\"Another tab active\"" ? label.Trim('"') : "other";
                                interruptions.Add(new { seconds = (int)timer.Elapsed.TotalSeconds, status, transportConnected = _boards.Values.All(board => board.Connected) });
                            }
                        }
                        if (disconnectedSamples != 0) Environment.ExitCode = 1;
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mcc-native-connection-observation.json"), JsonSerializer.Serialize(new { seconds = _memorySeconds, disconnectedSamples, interruptions, transportEvents = _connectionDiagnostics.ToArray() }));
                        Console.WriteLine($"Connection observation: {disconnectedSamples} disconnected samples over {_memorySeconds} seconds.");
                    }
                    Close();
                };
            else if (_updater is not null)
                _web.CoreWebView2.NavigationCompleted += async (_, e) => {
                    if (!e.IsSuccess || _updatePrompted) return;
                    _updatePrompted = true;
                    bool installing = false;
                    try
                    {
                        var available = await _updater.CheckAsync();
                        if (available is null || IsDisposed || _closing || _firmwareDialog?.IsBusy == true || _downloads?.Status.Status == "running") return;
                        if (MessageBox.Show(this, $"MeshCore Command {available.Version} is available. Download and install it now? The app will close and restart.",
                            "MeshCore Command update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        installing = true;
                        _web.Enabled = false; if (_firmwareDialog is not null) _firmwareDialog.Enabled = false;
                        try { await _updater.StartAsync(available.Version); BeginInvoke(Close); }
                        catch { _web.Enabled = true; if (_firmwareDialog is not null) _firmwareDialog.Enabled = true; throw; }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { if (!IsDisposed && !_closing) { if (installing) MessageBox.Show(this, "The update could not be started. " + error.Message, "MeshCore Command update", MessageBoxButtons.OK, MessageBoxIcon.Error); else System.Diagnostics.Debug.WriteLine("Update check: " + error.GetType().Name); } }
                };
            _web.Source = new Uri(_server.Origin + "/");
        }
        catch (Exception error) { if (_smokeTest) { Environment.ExitCode = 1; File.WriteAllText(Path.Combine(Path.GetTempPath(), "mcc-preview-test-error.txt"), error.ToString()); } else MessageBox.Show(this, error.Message, "Preview could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
    }

    private async Task HandleRequestAsync(string message)
    {
        long id = 0;
        object result;
        bool locked = false;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            id = root.GetProperty("id").GetInt64();
            string method = root.GetProperty("method").GetString() ?? "";
            var args = root.GetProperty("args");
            // Scans and GPS reads can take seconds; they do not mutate board sessions
            // and must not block window controls or incoming host requests.
            if (method is not ("gps_read" or "wifi_scan" or "board_scan_ble" or "check_app_update"))
            { await _requests.WaitAsync(); locked = true; }
            string Arg(int index) => args[index].GetString() ?? "";
            switch (method)
            {
                case "board_list":
                    result = new { boards = _profiles.List().Concat(_serialProfiles.List()).Where(p => !_connectedProfiles.ContainsKey(p.Id)).Concat(_connectedProfiles.Values).Select(p => new { id = p.Id, kind = p.Kind, address = p.Address, name = p.Name, open = _boards.ContainsKey(p.Id), connected = _boards.TryGetValue(p.Id, out var board) && board.Connected }).ToArray() }; break;
                case "board_connect_wifi":
                    EnsureExperimental();
                    if (!IPAddress.TryParse(Arg(0), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("Enter a valid IPv4 address.");
                    result = await ConnectAsync("wifi", ip.ToString(), async () => await WifiBoard.ConnectAsync(ip)); break;
                case "check_app_update":
                    if (_updater is null) throw new InvalidOperationException("Native updates are available only in an installed candidate.");
                    var release = await _updater.CheckAsync();
                    result = new { current = _updater.CurrentVersion, available = release is not null, version = release?.Version, prerelease = release?.Prerelease ?? false }; break;
                case "install_app_update":
                    if (_updater is null) throw new InvalidOperationException("Native updates are available only in an installed candidate.");
                    if (_firmwareDialog?.IsBusy == true || _downloads?.Status.Status == "running") throw new InvalidOperationException("Wait for firmware flashing and map downloads to finish before updating.");
                    _web.Enabled = false;
                    if (_firmwareDialog is not null) _firmwareDialog.Enabled = false;
                    try { await _updater.StartAsync(Arg(0)); }
                    catch { _web.Enabled = true; if (_firmwareDialog is not null) _firmwareDialog.Enabled = true; throw; }
                    result = new { ok = true }; BeginInvoke(Close); break;
                case "default_network":
                    try
                    {
                        using var route = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                        route.Connect("1.1.1.1", 80);
                        IPAddress local = ((IPEndPoint)route.LocalEndPoint!).Address;
                        var networkAddress = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                            .FirstOrDefault(item => item.Address.Equals(local));
                        result = new { network = WifiDiscovery.SuggestedSubnet(local, networkAddress?.PrefixLength ?? 24) };
                    }
                    catch (System.Net.Sockets.SocketException) { result = new { network = "" }; }
                    break;
                case "wifi_scan":
                    if (!_integrationTest || Arg(0) != "127.0.0.1/32") EnsureExperimental();
                    var progress = new Progress<DiscoveryProgress>(p => {
                        if (!IsDisposed && _web.CoreWebView2 is not null) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "scan-progress", attempted = p.Attempted, total = p.Total, waiting = p.Waiting }));
                    });
                    var radios = await WifiDiscovery.ScanAsync(Arg(0), progress);
                    result = new { devices = radios.Select(ip => new { ip = ip.ToString() }).ToArray() }; break;
                case "board_scan_ble":
                    EnsureExperimental();
                    result = new { devices = (await WindowsBleTransport.ScanAsync(TimeSpan.FromSeconds(10))).Select(d => new { name = d.Name, address = d.Address }).ToArray() }; break;
                case "board_connect_ble":
                    EnsureExperimental();
                    string address = Arg(0).ToUpperInvariant();
                    await WindowsBleTransport.PairAsync(address, Arg(1));
                    result = await ConnectAsync("ble", address, async () => new BleCompanion(await WindowsBleTransport.ConnectAsync(address))); break;
                case "board_connect_serial":
                    EnsureExperimental();
                    string port = Arg(0).ToUpperInvariant();
                    if (!UsbGps.Ports().Contains(port, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Select an available COM port.");
                    result = await ConnectAsync("serial", port, () => Task.FromResult<ICompanionBoard>(SerialCompanion.Open(port))); break;
                case "open_firmware":
                    if (!_experimentalFlash) throw new InvalidOperationException("Firmware flashing is disabled until the native flasher has been qualified.");
                    if (_firmwareDialog is null || _firmwareDialog.IsDisposed)
                    {
                        _firmwareDialog = new FirmwareDialog(port => _serialProfiles.List().Any(p => p.Address.Equals(port, StringComparison.OrdinalIgnoreCase) && _boards.ContainsKey(p.Id)));
                        _firmwareDialog.Show(this);
                    }
                    else _firmwareDialog.Activate();
                    result = new { ok = true }; break;
                case "open_hflink":
                    ShowHfLink();
                    result = new { ok = true }; break;
                case "board_rename":
                    var liveProfile = _connectedProfiles.GetValueOrDefault(Arg(0));
                    if (liveProfile is not null && !_profiles.List().Concat(_serialProfiles.List()).Any(p => p.Id == liveProfile.Id && p.Kind == liveProfile.Kind && p.Address == liveProfile.Address))
                        throw new InvalidOperationException("Disconnect this board before renaming; the restored saved connections differ from this session.");
                    if (_serialProfiles.List().Any(p => p.Id == Arg(0))) _serialProfiles.Rename(Arg(0), Arg(1)); else _profiles.Rename(Arg(0), Arg(1));
                    if (liveProfile is not null) _connectedProfiles[liveProfile.Id] = liveProfile with { Name = Arg(1).Trim() };
                    result = new { ok = true }; break;
                case "board_forget":
                    string forget = Arg(0);
                    if (_boards.ContainsKey(forget)) throw new InvalidOperationException("Disconnect the board before removing its saved connection.");
                    if (_serialProfiles.List().Any(p => p.Id == forget)) _serialProfiles.Remove(forget); else _profiles.Remove(forget);
                    result = new { ok = true }; break;
                case "board_disconnect":
                    string remove = Arg(0); _server!.RemoveBoard(remove); _boards.Remove(remove); _connectedProfiles.Remove(remove); await _sessions.RemoveAsync(remove); result = new { ok = true }; break;
                case "choose_offline_map":
                    using (var dialog = new OpenFileDialog { Filter = "Offline maps|*.mbtiles;*.pmtiles", CheckFileExists = true })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        _offlineMap.Select(dialog.FileName);
                        Directory.CreateDirectory(_dataDirectory);
                        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "offline-map.json"), JsonSerializer.Serialize(new { path = dialog.FileName }));
                        var info = _offlineMap.Info;
                        result = new { available = info.Available, name = info.Name, format = info.Format, maxZoom = info.MaxZoom };
                    }
                    break;
                case "save_terrain_plan":
                    string plan = Arg(0);
                    byte[] planBytes = System.Text.Encoding.UTF8.GetBytes(plan);
                    if (planBytes.Length > 1_000_000) throw new ArgumentException("Plan exceeds the 1 MB limit.");
                    using (var dialog = new SaveFileDialog { Filter = "MeshCore terrain plan|*.mccplan", FileName = "MeshCore-Terrain-Plan.mccplan" })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { await File.WriteAllBytesAsync(temporary, planBytes); File.Move(temporary, dialog.FileName, true); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        result = new { ok = true };
                    }
                    break;
                case "open_terrain_plan":
                    using (var dialog = new OpenFileDialog { Filter = "MeshCore terrain plan|*.mccplan", CheckFileExists = true })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        if (new FileInfo(dialog.FileName).Length > 1_000_000) throw new ArgumentException("Plan exceeds the 1 MB limit.");
                        result = new { contents = await File.ReadAllTextAsync(dialog.FileName) };
                    }
                    break;
                case "ports": result = await SerialPortDescriptions.ListAsync(); break;
                case "gps_read":
                    var fix = await UsbGps.ReadAsync(Arg(0), args[1].GetInt32(), TimeSpan.FromSeconds(8));
                    result = new { latitude = fix.Latitude, longitude = fix.Longitude }; break;
                case "export_full_backup":
                    using (var dialog = new SaveFileDialog { Filter = "MeshCore full backup|*.mccfull", FileName = "MeshCore-Command-Full-Backup.mccfull" })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        byte[] archive = BackupSnapshot.Create(_dataDirectory, Storage(args[0]), _credentials.ListSaved(), OptionalRadio(args, 2));
                        await WriteAtomicAsync(dialog.FileName, BackupEnvelope.Encrypt(archive, Arg(1)));
                        result = new { ok = true };
                    }
                    break;
                case "prepare_full_restore":
                    _pendingRestore = null; EnsureSingleBoard();
                    using (var dialog = new OpenFileDialog { Filter = "MeshCore backups|*.mccfull;*.mccbackup", CheckFileExists = true })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        if (new FileInfo(dialog.FileName).Length > BackupEnvelope.MaximumArchiveBytes + 1024L) throw new InvalidDataException("Backup exceeds the size limit.");
                        byte[] contents = await File.ReadAllBytesAsync(dialog.FileName);
                        if (!contents.AsSpan().StartsWith("MCCFULL1"u8))
                        {
                            if (contents.Length > 8_000_000) throw new InvalidDataException("Invalid or unsupported backup.");
                            using var legacy = JsonDocument.Parse(contents);
                            if (!legacy.RootElement.TryGetProperty("format", out var format) || format.GetString() != "MeshCoreCommandSecureBackup" ||
                                !legacy.RootElement.TryGetProperty("ciphertext", out var ciphertext) || ciphertext.ValueKind != JsonValueKind.String)
                                throw new InvalidDataException("Invalid or unsupported backup.");
                            result = new { legacy_secure = System.Text.Encoding.UTF8.GetString(contents) }; break;
                        }
                        byte[] archive = BackupEnvelope.Decrypt(contents, Arg(1));
                        var decoded = BackupArchive.Validate(archive);
                        string recovery = await SaveRecoveryAsync(Storage(args[0]), Arg(1), null);
                        var storage = Storage(decoded.Manifest.GetProperty("storage"));
                        _pendingRestore = archive; result = new { storage, recovery };
                    }
                    break;
                case "commit_full_restore":
                    EnsureSingleBoard();
                    if (_pendingRestore is null) throw new InvalidOperationException("No restore is ready.");
                    byte[] pending = _pendingRestore; _pendingRestore = null;
                    BackupRestore.Apply(pending, _dataDirectory, _credentials);
                    _profiles = new BoardProfiles(Path.Combine(_dataDirectory, "boards.json"));
                    _serialProfiles = new BoardProfiles(Path.Combine(_dataDirectory, "serial-boards.json"), allowSerial: true);
                    result = new { ok = true }; break;
                case "cancel_full_restore": _pendingRestore = null; result = new { ok = true }; break;
                case "save_pc_recovery": result = new { recovery = await SaveRecoveryAsync(Storage(args[0]), Arg(1), null) }; break;
                case "restore_local_credentials":
                    if (!_candidate) throw new InvalidOperationException("Local credential recovery is available only in the native candidate.");
                    if (_firmwareDialog?.IsBusy == true || _downloads?.Status.Status == "running") throw new InvalidOperationException("Wait for firmware flashing and map downloads to finish before restoring credentials.");
                    EnsureSingleBoard();
                    string registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Candidate Data");
                    using (var dialog = new OpenFileDialog { Filter = "Local credential recovery|*.mcccred", InitialDirectory = Path.Combine(registry, "recovery"), CheckFileExists = true })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        string recovery = await CredentialRecovery.CreateAsync(registry, _credentials);
                        try { CredentialRecovery.Restore(dialog.FileName, _credentials); }
                        catch (Exception error) { throw new IOException("Credential restore failed. A recovery copy was saved at " + recovery + ". " + error.Message, error); }
                        result = new { ok = true, recovery }; BeginInvoke(Close);
                    }
                    break;
                case "save_radio_recovery": result = new { recovery = await SaveRecoveryAsync(Storage(args[0]), Arg(1), OptionalRadio(args, 2) ?? throw new ArgumentException("Current radio backup is missing.")) }; break;
                case "open_radio_backup":
                    EnsureSingleBoard();
                    using (var dialog = new OpenFileDialog { Filter = "MeshCore full backup|*.mccfull", CheckFileExists = true })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) { result = new { cancelled = true }; break; }
                        if (new FileInfo(dialog.FileName).Length > BackupEnvelope.MaximumArchiveBytes + 1024L) throw new InvalidDataException("Backup exceeds the size limit.");
                        var decoded = BackupArchive.Validate(BackupEnvelope.Decrypt(await File.ReadAllBytesAsync(dialog.FileName), Arg(0)));
                        if (!decoded.Manifest.TryGetProperty("radio", out var radio) || radio.ValueKind != JsonValueKind.Object) throw new InvalidDataException("This backup has no radio configuration.");
                        result = new { radio };
                    }
                    break;
                case "minimize": WindowState = FormWindowState.Minimized; result = new { ok = true }; break;
                case "toggle_fullscreen": ToggleFullScreen(); result = new { ok = true }; break;
                case "close": result = new { ok = true }; BeginInvoke(Close); break;
                default: result = new { error = "This feature is not implemented in the development preview." }; break;
            }
        }
        catch (Exception error) { result = new { error = error.Message }; }
        finally { if (locked) _requests.Release(); }
        if (!IsDisposed && _web.CoreWebView2 is not null)
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "result", id, value = result }));
    }

    private void ShowHfLink()
    {
        if (_closing) throw new InvalidOperationException("MeshCore Command is closing.");
        if (_hfLinkView is null)
        {
            string directory = _dataDirectory;
            if (_integrationTest && _integrationIp is null)
            {
                _hfLinkTestDirectory = Directory.CreateTempSubdirectory("mcc-hflink-preview-").FullName;
                directory = _hfLinkTestDirectory;
                var fixture = new HfLink(Path.Combine(directory, "hflink-native.sqlite3"));
                fixture.SaveStation(new HfStation("M0ABC"));
                fixture.Queue("G1XYZ", "Example message from the local operator");
            }
            _hfLinkView = new HfLinkView(directory, _experimentalRadio && !_smokeTest, ReturnToRadioAsync, NotifyHfLinkMessage);
            _content.Controls.Add(_hfLinkView);
        }
        _web.Hide();
        _hfLinkView.Show();
        _hfLinkView.BringToFront();
        _hfLinkView.Select();
        _hfLinkView.EnterView();
    }

    private void NotifyHfLinkMessage(HfMessage message)
    {
        if (_closing || (_hfLinkView?.Visible == true && WindowState != FormWindowState.Minimized)) return;
        string title = message.Simulated ? "HFLink simulation" : "HFLink message received";
        _notifications.Show(title, $"From {message.From}\n{message.Text}", () => OpenHfLinkMessage(message));
    }

    private void OpenHfLinkMessage(HfMessage message)
    {
        if (_closing) return;
        RestoreAndActivate();
        ShowHfLink();
        _hfLinkView!.SelectMessage(message, clearFilter: true);
    }

    private async Task<bool> CheckHfLinkReceiveAsync(HfLinkView view, Button back)
    {
        if (view.Controls.Find("hfLinkSettings", true).SingleOrDefault() is not Button { Text: "Settings" } ||
            view.Controls.Find("hfLinkReceive", true).SingleOrDefault() is not Button { Enabled: true } receive) return false;
        string? gallery = _candidate ? null : Environment.GetEnvironmentVariable("MCC_HFLINK_GALLERY_DIR");
        if (!string.IsNullOrEmpty(gallery))
        {
            Directory.CreateDirectory(gallery);
            using var settings = view.CreateSettingsDialog();
            settings.Show(this);
            await Task.Yield();
            CaptureControl(settings, Path.Combine(gallery, "hflink-settings.png"));
            settings.Close();
        }
        receive.PerformClick();
        back.PerformClick();
        if (!_web.Visible || view.Visible) return false;
        Form? notification = null;
        for (int attempt = 0; attempt < 50 && notification is null; attempt++)
        {
            await Task.Delay(100);
            notification = Application.OpenForms.Cast<Form>().FirstOrDefault(form =>
                form.Text == "MeshCore Command notification" &&
                form.Controls.OfType<Label>().Any(label => label.Text == "HFLink simulation"));
        }
        if (notification is null || !_web.Visible) return false;
        if (!string.IsNullOrEmpty(gallery))
        {
            using var preview = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            preview.Position = 0;
            using var dashboard = new Bitmap(preview);
            using var overlay = new Bitmap(notification.Width, notification.Height);
            notification.DrawToBitmap(overlay, new Rectangle(Point.Empty, overlay.Size));
            using (var graphics = Graphics.FromImage(dashboard))
                graphics.DrawImageUnscaled(overlay, dashboard.Width - overlay.Width - 16, dashboard.Height - overlay.Height - 16);
            dashboard.Save(Path.Combine(gallery, "hflink-notification.png"));
        }
        notification.Close();
        var fixture = new HfLink(Path.Combine(_hfLinkTestDirectory!, "hflink-native.sqlite3"));
        var incoming = fixture.Messages().Where(message => message.Incoming).ToArray();
        if (incoming.Length != 1) return false;
        ShowHfLink();
        view.SelectMessage(incoming[0], clearFilter: true);
        if (!string.IsNullOrEmpty(gallery))
            CaptureControl(view, Path.Combine(gallery, "hflink.png"));
        await view.StopAsync();
        return view.Visible && !_web.Visible && fixture.Messages().Count(message => message.Incoming) == 1;
    }

    private static void CaptureControl(Control control, string path)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path);
    }

    private static bool CheckHfLinkButtonContrast(HfLinkView view)
    {
        if (SystemInformation.HighContrast) return true;
        if (view.Controls.Find("hfLinkStopOperation", true).SingleOrDefault() is not Button { Enabled: false } button ||
            button.Width < 3 || button.Height < 3) return false;
        using var bitmap = new Bitmap(button.Width, button.Height);
        button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, button.Size));
        // Sample the painted interior. The background and border are both deliberately dark.
        for (int y = 2; y < bitmap.Height - 2; y++)
            for (int x = 2; x < bitmap.Width - 2; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R >= 125 && pixel.G >= 140 && pixel.B >= 150) return true;
            }
        return false;
    }

    private async Task ReturnToRadioAsync()
    {
        if (_hfLinkView is null) return;
        await _hfLinkView.LeaveViewAsync();
        if (_closing) return;
        _hfLinkView.Hide();
        _web.Show();
        _web.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11 && _hfLinkView?.Visible == true)
        {
            ToggleFullScreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RestoreAndActivate()
    {
        if (IsDisposed) return;
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
            if (_fullscreen) Bounds = Screen.FromControl(this).Bounds;
        }
        Activate();
    }

    private void ToggleFullScreen()
    {
        if (!_fullscreen)
        {
            _previousBorder = FormBorderStyle;
            _previousState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            _previousBounds = _previousState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var screenBounds = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screenBounds;
            _fullscreen = true;
        }
        else
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _previousBorder;
            Bounds = _previousBounds;
            WindowState = _previousState;
            _fullscreen = false;
        }
    }

    private void EnsureExperimental()
    {
        if (!_experimentalRadio) throw new InvalidOperationException("Radio connections are disabled in this development preview.");
    }

    private void EnsureSingleBoard()
    {
        if (_boards.Count > 1) throw new InvalidOperationException("Disconnect the other boards before restoring a backup.");
    }

    private static Dictionary<string, string> Storage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid app storage.");
        var result = JsonSerializer.Deserialize<Dictionary<string, string>>(value.GetRawText()) ?? throw new InvalidDataException("Invalid app storage.");
        foreach (string key in result.Keys.Where(key => key == "rcc6-session" || key == "rcc6-radio-owner" || key.StartsWith("rcc6-session:", StringComparison.Ordinal) || key.StartsWith("rcc6-radio-owner:", StringComparison.Ordinal)).ToArray()) result.Remove(key);
        return result;
    }

    private static JsonElement? OptionalRadio(JsonElement args, int index) => args.GetArrayLength() > index && args[index].ValueKind != JsonValueKind.Null ? args[index].Clone() : null;

    private async Task<string> SaveRecoveryAsync(Dictionary<string, string> storage, string password, JsonElement? radio)
    {
        string folder = Path.Combine(_dataDirectory, "recovery-backups"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".mccfull");
        var archive = BackupSnapshot.Create(_dataDirectory, storage, _credentials.ListSaved(), radio);
        await WriteAtomicAsync(path, BackupEnvelope.Encrypt(archive, password));
        return path;
    }

    private static async Task WriteAtomicAsync(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            { await file.WriteAsync(data); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<object> ConnectAsync(string kind, string address, Func<Task<ICompanionBoard>> connect)
    {
        var registry = kind == "serial" ? _serialProfiles : _profiles;
        var profile = registry.List().FirstOrDefault(p => p.Kind == kind && p.Address == address) ?? registry.Add(kind, address, $"Board {_profiles.List().Count + _serialProfiles.List().Count + 1}");
        if (_boards.ContainsKey(profile.Id)) throw new InvalidOperationException("This board is already connected.");
        var board = await connect();
        await _sessions.AddAsync(profile.Id, board);
        try { _server!.AddBoard(profile.Id, board); _boards.Add(profile.Id, board); _connectedProfiles.Add(profile.Id, profile); }
        catch { _server?.RemoveBoard(profile.Id); _boards.Remove(profile.Id); _connectedProfiles.Remove(profile.Id); await _sessions.RemoveAsync(profile.Id); throw; }
        return new { id = profile.Id };
    }
}

internal sealed class SyntheticBoard(string message, Func<bool>? canDeliver = null) : ICompanionBoard
{
    public FrameQueue Received { get; } = new();
    public bool Connected => true;
    private bool _messageSent;
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        byte[] request = payload.ToArray();
        if (request[0] == 10 && canDeliver is not null)
        {
            for (int attempt = 0; attempt < 100 && !canDeliver(); attempt++) await Task.Delay(100, cancellationToken);
            if (!canDeliver()) throw new TimeoutException("Synthetic startup did not become ready.");
        }
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        switch (request[0])
        {
            case 1:
                writer.Write((byte)5); writer.Write((byte)1); writer.Write((byte)20); writer.Write((byte)22);
                writer.Write(Enumerable.Repeat((byte)7, 32).ToArray()); writer.Write(0); writer.Write(0); writer.Write(new byte[3]); writer.Write((byte)0);
                writer.Write((uint)869618); writer.Write((uint)62500); writer.Write((byte)8); writer.Write((byte)5); writer.Write(Encoding.UTF8.GetBytes("Synthetic radio\0")); break;
            case 22:
                writer.Write((byte)13); writer.Write((byte)3); writer.Write(new byte[6]); writer.Write(new byte[12]); writer.Write(Encoding.UTF8.GetBytes("Synthetic\0")); break;
            case 5: writer.Write((byte)9); writer.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); break;
            case 4: writer.Write((byte)4); writer.Write((uint)0); break;
            case 20: writer.Write((byte)12); writer.Write((ushort)4000); break;
            case 31 when request.Length > 1 && request[1] == 0:
                writer.Write((byte)18); writer.Write((byte)0); var name = new byte[32]; Encoding.UTF8.GetBytes("Public").CopyTo(name, 0); writer.Write(name); writer.Write(new byte[16]); break;
            case 10 when !_messageSent:
                _messageSent = true; writer.Write((byte)8); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); writer.Write(Encoding.UTF8.GetBytes("Synthetic: " + message + "\0")); break;
            case 10: writer.Write((byte)10); break;
            default: writer.Write((byte)1); writer.Write((byte)1); break;
        }
        Received.Enqueue(output.ToArray());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class PreviewCredentials : ICredentialStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> _items = new();
    public string? Get(string scope, string key) => _items.GetValueOrDefault((scope, key));
    public void Put(string scope, string key, string value) => _items[(scope, key)] = value;
    public void Delete(string scope, string key) => _items.TryRemove((scope, key), out _);
    public IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null) => _items.Where(item => (scope is null || item.Key.Item1 == scope) && (key is null || item.Key.Item2 == key)).ToDictionary(item => item.Key.Item1 + "/" + item.Key.Item2, item => item.Value);
}
