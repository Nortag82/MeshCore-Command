using MeshCore.Windows.Core;
using System.Net;
using System.Net.Sockets;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

if (args.Contains("--live-discovery-test"))
{
    string value = args.FirstOrDefault(arg => arg.StartsWith("--address=", StringComparison.Ordinal))?[10..] ?? throw new ArgumentException("Specify a test board address.");
    var expected = IPAddress.Parse(value);
    var results = await WifiDiscovery.ScanAsync(expected + "/32", responseWait: TimeSpan.FromSeconds(5));
    Check(results.Contains(expected), "The test board did not answer discovery.");
    Console.WriteLine("Live Wi-Fi discovery passed.");
    return;
}

if (args.Contains("--live-map-test"))
{
    string liveDirectory = Path.Combine(Path.GetTempPath(), "mcc-map-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(liveDirectory);
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCoreCommandNativeTest/1.0");
        var maps = new OfflineMapStore();
        string? saved = null;
        await using var download = new OfflineDownloads(http, liveDirectory, maps, path => saved = path);
        download.Start(new[] { -4.733, 55.999, -4.731, 56.001 }, 8);
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (download.Status.Status == "running" && DateTime.UtcNow < deadline) await Task.Delay(1000);
        Check(download.Status.Status == "complete", "Live map download: " + download.Status.Message);
        Check(saved is not null && File.Exists(saved) && maps.Info.Available && maps.Info.Format == "pmtiles", "Downloaded map is selected and readable");
        Console.WriteLine("Live map download and selection passed.");
    }
    finally { Directory.Delete(liveDirectory, recursive: true); }
    return;
}

await HfLinkChecks.RunAsync();
var output = CompanionFrames.EncodeOutgoing(new byte[] { 1, 2, 3 });
{
    string local = Path.Combine(Path.GetTempPath(), "mcc-retirement-test-" + Guid.NewGuid().ToString("N"));
    string install = Path.Combine(local, "MeshCore Command"), data = Path.Combine(local, "MeshCore Command Data");
    Directory.CreateDirectory(install); Directory.CreateDirectory(data);
    string executable = Path.Combine(install, "MeshCore Command.exe"), userFile = Path.Combine(data, "settings.json");
    File.WriteAllText(executable, "old program"); File.WriteAllText(userFile, "saved settings");
    try
    {
        await ThrowsAsync<IOException>(() => Task.Run(() => LegacyProgramRetirement.Retire(local, Path.Combine(data, "MeshCore Command.exe"))));
        Check(File.Exists(executable) && File.Exists(userFile), "An unexpected legacy location is never retired");
        string backup = LegacyProgramRetirement.Retire(local, executable);
        Check(!Directory.Exists(install) && File.ReadAllText(Path.Combine(backup, "MeshCore Command.exe")) == "old program" &&
              File.ReadAllText(userFile) == "saved settings", "Retirement moves only the old program and keeps data and rollback copy");
        await ThrowsAsync<IOException>(() => Task.Run(() => LegacyProgramRetirement.Retire(local, executable)));
    }
    finally { Directory.Delete(local, recursive: true); }
}
Check(LegacyUpdateHandoff.Parse(new[] { "--update", "42" }) == 42, "Legacy updater handoff accepts a process ID");
foreach (string[] invalid in new[] { Array.Empty<string>(), new[] { "--update" }, new[] { "--update", "0" }, new[] { "--update", "-1" }, new[] { "--update", " 42" }, new[] { "--update", "42", "extra" }, new[] { "--native-update", "42" } })
    await ThrowsAsync<ArgumentException>(() => Task.Run(() => LegacyUpdateHandoff.Parse(invalid)));
{
    var steps = new List<string>();
    string[] handoff = { "--update", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    string executable = Path.Combine(Path.GetTempPath(), "mcc-no-such-legacy-" + Guid.NewGuid().ToString("N") + ".exe");
    Task Record(string value) { steps.Add(value); return Task.CompletedTask; }
    await LegacyUpgradeTransaction.RunAsync(handoff, executable, _ => Record("prepare"), _ => Record("ready"), _ => Record("shortcuts"), TimeSpan.FromSeconds(1));
    Check(steps.SequenceEqual(new[] { "prepare", "ready", "shortcuts" }), "Legacy upgrade switches shortcuts only after readiness");
    steps.Clear();
    await ThrowsAsync<IOException>(() => LegacyUpgradeTransaction.RunAsync(handoff, executable, _ => Record("prepare"), _ => Task.FromException(new IOException("Synthetic startup failure")), _ => Record("shortcuts"), TimeSpan.FromSeconds(1)));
    Check(steps.SequenceEqual(new[] { "prepare" }), "Failed startup leaves legacy shortcuts unchanged");
    steps.Clear();
    await ThrowsAsync<ArgumentException>(() => LegacyUpgradeTransaction.RunAsync(new[] { "--update", "bad" }, executable, _ => Record("prepare"), _ => Record("ready"), _ => Record("shortcuts"), TimeSpan.FromSeconds(1)));
    Check(steps.Count == 0, "Invalid legacy handoff does not prepare or change installation");
    string runningExecutable = Environment.ProcessPath ?? throw new IOException("Test executable is unavailable.");
    await ThrowsAsync<IOException>(() => Task.Run(() => LegacyProcessGuard.EnsureStopped(runningExecutable)));
    steps.Clear();
    await ThrowsAsync<IOException>(() => LegacyUpgradeTransaction.RunAsync(handoff, runningExecutable, _ => Record("prepare"), _ => Record("ready"), _ => Record("shortcuts"), TimeSpan.FromSeconds(1)));
    Check(steps.Count == 0, "Another legacy process prevents migration and shortcut changes");
}
if (OperatingSystem.IsWindows())
{
    string testDirectory = Path.Combine(Path.GetTempPath(), "mcc-legacy-reopen-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testDirectory);
    string executable = Path.Combine(testDirectory, "mcc-legacy-" + Guid.NewGuid().ToString("N") + ".exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
    try
    {
        foreach (bool reopenAfterStartup in new[] { false, true })
        {
            using var started = new System.Threading.ManualResetEventSlim();
            System.Diagnostics.Process? reopened = null;
            bool candidateStarted = false, shortcutsSwitched = false;
            Task Reopen()
            {
                var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
                reopened = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start legacy process test.");
                started.Set();
                return Task.CompletedTask;
            }
            try
            {
                await ThrowsAsync<IOException>(() => LegacyUpgradeTransaction.RunAsync(
                    new[] { "--update", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) }, executable,
                    _ => reopenAfterStartup ? Task.CompletedTask : Reopen(),
                    async _ => { candidateStarted = true; if (reopenAfterStartup) await Reopen(); },
                    _ => { shortcutsSwitched = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1)));
                Check(started.IsSet && candidateStarted == reopenAfterStartup && !shortcutsSwitched,
                    "Reopening the old app stops the upgrade before shortcuts are changed");
            }
            finally
            {
                if (reopened is not null)
                {
                    try { if (!reopened.HasExited) reopened.Kill(entireProcessTree: true); await reopened.WaitForExitAsync(); }
                    finally { reopened.Dispose(); }
                }
            }
        }
    }
    finally { Directory.Delete(testDirectory, recursive: true); }
}
using (var self = System.Diagnostics.Process.GetCurrentProcess())
{
    long start = self.StartTime.ToUniversalTime().Ticks;
    await ThrowsAsync<InvalidOperationException>(() => UpdateParentGate.WaitAsync(self.Id, start + 1, TimeSpan.FromMilliseconds(100)));
    await ThrowsAsync<TimeoutException>(() => UpdateParentGate.WaitAsync(self.Id, start, TimeSpan.FromMilliseconds(100)));
    await UpdateParentGate.WaitAsync(int.MaxValue, start, TimeSpan.FromMilliseconds(100));
    string ownExecutable = self.MainModule!.FileName;
    await ThrowsAsync<InvalidOperationException>(() => LegacyUpdateParentGate.WaitAsync(self.Id, ownExecutable + ".different", TimeSpan.FromMilliseconds(100)));
    await ThrowsAsync<TimeoutException>(() => LegacyUpdateParentGate.WaitAsync(self.Id, ownExecutable, TimeSpan.FromMilliseconds(100)));
    await LegacyUpdateParentGate.WaitAsync(int.MaxValue, ownExecutable, TimeSpan.FromMilliseconds(100));
}
if (OperatingSystem.IsWindows())
{
    string childExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    var start = new System.Diagnostics.ProcessStartInfo(childExecutable) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Seconds 6");
    using var child = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start update wait test process.");
    bool childReady = false;
    for (int attempt = 0; attempt < 100 && !child.HasExited; attempt++)
    {
        using var observed = System.Diagnostics.Process.GetProcessById(child.Id);
        string? actual = observed.MainModule?.FileName;
        if (string.Equals(actual, childExecutable, StringComparison.OrdinalIgnoreCase)) { childReady = true; break; }
        await Task.Delay(20);
    }
    Check(childReady, "Update wait test process reached its expected executable");
    await Task.WhenAll(UpdateParentGate.WaitAsync(child.Id, child.StartTime.ToUniversalTime().Ticks, TimeSpan.FromSeconds(10)),
        LegacyUpdateParentGate.WaitAsync(child.Id, childExecutable, TimeSpan.FromSeconds(10)));
    Check(child.HasExited && child.ExitCode == 0, "Updater waits for the matching process to exit normally");
}
Check(output.SequenceEqual(new byte[] { (byte)'<', 3, 0, 1, 2, 3 }), "Outgoing frame encoding");
using (var input = new MemoryStream(new byte[] { 0, 0xff, (byte)'>', 3, 0, 4, 5, 6 }))
    Check((await CompanionFrames.ReadAsync(input)).SequenceEqual(new byte[] { 4, 5, 6 }), "Incoming frame and noise");
await ThrowsAsync<InvalidDataException>(async () => await CompanionFrames.ReadAsync(new MemoryStream(new byte[] { (byte)'>', 177, 0 })));
await ThrowsAsync<EndOfStreamException>(async () => await CompanionFrames.ReadAsync(new MemoryStream(new byte[] { (byte)'>', 2, 0, 1 })));
await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() => CompanionFrames.EncodeOutgoing(Array.Empty<byte>())));

var queue = new FrameQueue(2);
queue.Enqueue(new byte[] { 7 });
queue.Enqueue(new byte[] { 8 });
var first = queue.PeekAfterAcknowledgement(null)!;
Check(first.Sequence == 1 && first.Payload[0] == 7, "First queued frame");
first.Payload[0] = 0;
Check(queue.PeekAfterAcknowledgement(99)!.Payload[0] == 7, "Incorrect ack and defensive copy");
Check(queue.PeekAfterAcknowledgement(1)!.Sequence == 2, "Acknowledgement advances queue");
Check(queue.PeekAfterAcknowledgement(2) is null, "Acknowledgement empties queue");
queue.Enqueue(new byte[] { 1 }); queue.Enqueue(new byte[] { 2 });
await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => queue.Enqueue(new byte[] { 3 })));
var identityBoard = new IdentityTestBoard();
identityBoard.Received.Enqueue(new byte[] { 77 });
var identified = await BoardIdentity.ReadAsync(identityBoard, TimeSpan.FromSeconds(1));
Check(identified.Length == 32 && identified[0] == 12 && identityBoard.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 77,
    "Board identification preserves unrelated frames");
Check(await CompanionDiagnostics.ReadBatteryMillivoltsAsync(identityBoard, TimeSpan.FromSeconds(1)) == 4000 && identityBoard.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 77,
    "Battery query preserves unrelated frames");
await using (var sessions = new BoardSessions())
{
    await sessions.AddAsync("one", new IdentityTestBoard());
    await ThrowsAsync<InvalidOperationException>(() => sessions.AddAsync("two", new IdentityTestBoard()));
}

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var accept = listener.AcceptTcpClientAsync();
    await using var radio = await WifiCompanion.ConnectAsync(IPAddress.Loopback, port);
    using var peer = await accept;
    using var stream = peer.GetStream();
    await radio.SendAsync(new byte[] { 9, 10 });
    var outgoing = new byte[5];
    await stream.ReadExactlyAsync(outgoing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(outgoing.SequenceEqual(new byte[] { (byte)'<', 2, 0, 9, 10 }), "Wi-Fi outgoing packet");
    await stream.WriteAsync(new byte[] { (byte)'>', 1, 0, 42 });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (radio.Received.PeekAfterAcknowledgement(null) is null)
        await Task.Delay(10, timeout.Token);
    Check(radio.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 42, "Wi-Fi incoming packet");
}
finally { listener.Stop(); }

var serialListener = new TcpListener(IPAddress.Loopback, 0);
serialListener.Start();
try
{
    int port = ((IPEndPoint)serialListener.LocalEndpoint).Port;
    var accept = serialListener.AcceptTcpClientAsync();
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
    using var peer = await accept;
    await using var serial = SerialCompanion.FromStream(client.GetStream());
    await serial.SendAsync(new byte[] { 31 });
    var outgoing = new byte[4]; await peer.GetStream().ReadExactlyAsync(outgoing).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(outgoing.SequenceEqual(new byte[] { (byte)'<', 1, 0, 31 }), "Serial stream outgoing frame");
    await peer.GetStream().WriteAsync(new byte[] { (byte)'>', 1, 0, 32 });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (serial.Received.PeekAfterAcknowledgement(null) is null) await Task.Delay(10, timeout.Token);
    Check(serial.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 32, "Serial stream incoming frame");
}
finally { serialListener.Stop(); }
var fakeBle = new TestBleTransport();
await using (var ble = new BleCompanion(fakeBle))
{
    await ble.SendAsync(new byte[] { 44 });
    Check(fakeBle.LastWrite!.SequenceEqual(new byte[] { 44 }), "BLE sends raw packet without stream framing");
    fakeBle.Receive(new byte[] { 45 });
    Check(ble.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 45, "BLE notification received");
    fakeBle.Receive(Array.Empty<byte>());
    Check(!ble.Connected, "Invalid BLE packet closes session");
}

var reconnectListener = new TcpListener(IPAddress.Loopback, 0);
reconnectListener.Start();
try
{
    int port = ((IPEndPoint)reconnectListener.LocalEndpoint).Port;
    var incoming = reconnectListener.AcceptTcpClientAsync();
    await using var board = await WifiBoard.ConnectAsync(IPAddress.Loopback, port, initialDelay: TimeSpan.FromMilliseconds(20), maxDelay: TimeSpan.FromMilliseconds(100));
    var firstPeer = await incoming;
    await firstPeer.GetStream().WriteAsync(new byte[] { (byte)'>', 1, 0, 11 });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (board.Received.PeekAfterAcknowledgement(null) is null) await Task.Delay(10, timeout.Token);
    firstPeer.Dispose();
    using var secondPeer = await reconnectListener.AcceptTcpClientAsync(timeout.Token);
    await secondPeer.GetStream().WriteAsync(new byte[] { (byte)'>', 1, 0, 12 });
    Check(board.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 11, "Queue survives reconnect");
    while (board.Received.PeekAfterAcknowledgement(1) is null) await Task.Delay(10, timeout.Token);
    Check(board.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 12, "Receive after reconnect");
}
finally { reconnectListener.Stop(); }

var flappingListener = new TcpListener(IPAddress.Loopback, 0);
flappingListener.Start();
try
{
    int port = ((IPEndPoint)flappingListener.LocalEndpoint).Port;
    var firstFlappingPeer = flappingListener.AcceptTcpClientAsync();
    await using var board = await WifiBoard.ConnectAsync(IPAddress.Loopback, port, initialDelay: TimeSpan.FromMilliseconds(150), maxDelay: TimeSpan.FromMilliseconds(600));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var peer = await firstFlappingPeer;
    var waits = new List<double>();
    for (int i = 0; i < 3; i++)
    {
        peer.Dispose();
        var started = System.Diagnostics.Stopwatch.StartNew();
        peer = await flappingListener.AcceptTcpClientAsync(timeout.Token);
        waits.Add(started.Elapsed.TotalMilliseconds);
        while (!board.Connected) await Task.Delay(5, timeout.Token);
    }
    peer.Dispose();
    Check(waits[1] >= 220 && waits[2] >= 450, "Repeated short connections use increasing reconnect delays");
}
finally { flappingListener.Stop(); }

var identityListener = new TcpListener(IPAddress.Loopback, 0);
identityListener.Start();
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    async Task IdentifyPeer(TcpClient peer, byte identity, byte? queued = null)
    {
        var stream = peer.GetStream(); byte[] header = new byte[3];
        await stream.ReadExactlyAsync(header, deadline.Token);
        Check(header[0] == '<', "Identity request has companion frame header");
        byte[] payload = new byte[header[1] | (header[2] << 8)]; await stream.ReadExactlyAsync(payload, deadline.Token);
        Check(payload[0] == 1, "Reconnect requests board identity");
        if (queued is byte value) await stream.WriteAsync(new byte[] { (byte)'>', 1, 0, value }, deadline.Token);
        byte[] reply = new byte[39]; reply[0] = (byte)'>'; reply[1] = 36; reply[3] = 5;
        Array.Fill(reply, identity, 7, 32); await stream.WriteAsync(reply, deadline.Token);
    }
    var accept = identityListener.AcceptTcpClientAsync(deadline.Token);
    var board = await WifiBoard.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)identityListener.LocalEndpoint).Port, initialDelay: TimeSpan.FromMilliseconds(20), maxDelay: TimeSpan.FromMilliseconds(100));
    await using var sessions = new BoardSessions();
    using var firstIdentityPeer = await accept;
    Task add = sessions.AddAsync("test", board, deadline.Token); await IdentifyPeer(firstIdentityPeer, 7); await add;
    firstIdentityPeer.Dispose();
    using var wrong = await identityListener.AcceptTcpClientAsync(deadline.Token);
    await IdentifyPeer(wrong, 8, 99);
    using var correct = await identityListener.AcceptTcpClientAsync(deadline.Token);
    await IdentifyPeer(correct, 7, 13);
    while (!board.Connected || board.Received.PeekAfterAcknowledgement(null) is null) await Task.Delay(10, deadline.Token);
    var verifiedFrame = board.Received.PeekAfterAcknowledgement(null)!;
    Check(verifiedFrame.Payload[0] == 13, "Reconnect rejects the wrong identity and preserves verified radio frames");
    await correct.GetStream().WriteAsync(new byte[] { (byte)'>', 1, 0, 14 }, deadline.Token);
    while (board.Received.PeekAfterAcknowledgement(verifiedFrame.Sequence) is null) await Task.Delay(10, deadline.Token);
    Check(board.Received.PeekAfterAcknowledgement(null)!.Payload[0] == 14, "Verified reconnect continues forwarding new frames");
}
finally { identityListener.Stop(); }

var endpointRadio = new TcpListener(IPAddress.Loopback, 0);
endpointRadio.Start();
try
{
    int radioPort = ((IPEndPoint)endpointRadio.LocalEndpoint).Port;
    var accepting = endpointRadio.AcceptTcpClientAsync();
    await using var board = await WifiBoard.ConnectAsync(IPAddress.Loopback, radioPort);
    using var peer = await accepting;
    var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
    int httpPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    string boardId = "0123456789abcdef0123456789abcdef";
    var endpointCredentials = new TestCredentials();
    string? notifiedTitle = null;
    string endpointMap = Path.Combine(Path.GetTempPath(), "mcc-map-test-" + Guid.NewGuid().ToString("N") + ".pmtiles");
    byte[] endpointMapBytes = new byte[120]; "PMTiles\x03"u8.CopyTo(endpointMapBytes); File.WriteAllBytes(endpointMap, endpointMapBytes);
    var endpointMaps = new OfflineMapStore(); endpointMaps.Select(endpointMap);
    await using var endpoint = new BoardFrameEndpoint(httpPort, System.Text.Encoding.UTF8.GetBytes("<html>shell</html>"), "<html><head></head><body>radio</body></html>", endpointCredentials, endpointMaps, notify: (title, body) => notifiedTitle = title);
    endpoint.AddBoard(boardId, board);
    string secondId = "abcdef0123456789abcdef0123456789";
    var secondBoard = new RecordingBoard(); endpoint.AddBoard(secondId, secondBoard);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    string url = $"{endpoint.Origin}/board/{boardId}/api/frame";
    Check((await http.GetStringAsync(endpoint.Origin + "/")).Contains("shell"), "Serve isolated desktop shell");
    Check((await http.GetStringAsync($"{endpoint.Origin}/board/{boardId}/")).Contains("window.__MCC_BOARD_FIRST=true"), "Serve board interface with isolated board context");
    Check((await http.GetStringAsync($"{endpoint.Origin}/board/{secondId}/")).Contains("window.__MCC_BOARD_FIRST=false"), "Second board has a separate view context");
    Check((await http.GetAsync(url)).StatusCode == HttpStatusCode.Forbidden, "Endpoint rejects missing token");
    using var forbidden = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(new byte[] { 9 }) };
    forbidden.Headers.Add("X-MCC-Board-Token", endpoint.Token);
    Check((await http.SendAsync(forbidden)).StatusCode == HttpStatusCode.Forbidden, "Endpoint rejects missing origin");
    string credentialUrl = $"{endpoint.Origin}/board/{boardId}/api/secure-credential";
    using var credentialRequest = new HttpRequestMessage(HttpMethod.Post, credentialUrl) { Content = new StringContent("{\"action\":\"save\",\"key\":\"" + new string('a', 64) + "\",\"password\":\"synthetic\"}") };
    credentialRequest.Headers.Add("X-MCC-Board-Token", endpoint.Token); credentialRequest.Headers.Add("Origin", endpoint.Origin);
    Check((await http.SendAsync(credentialRequest)).StatusCode == HttpStatusCode.OK && endpointCredentials.Value == "synthetic", "Authorized credential storage");
    using var notificationRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.Origin}/board/{boardId}/api/notify") { Content = new StringContent("{\"title\":\"test\",\"body\":\"message\"}") };
    notificationRequest.Headers.Add("X-MCC-Board-Token", endpoint.Token); notificationRequest.Headers.Add("Origin", endpoint.Origin);
    Check((await http.SendAsync(notificationRequest)).StatusCode == HttpStatusCode.OK && notifiedTitle == "test", "Authorized notification");
    using var mapRequest = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.Origin}/board/{boardId}/api/offline-map.pmtiles");
    mapRequest.Headers.Add("X-MCC-Board-Token", endpoint.Token); mapRequest.Headers.Add("Range", "bytes=0-7");
    using var mapResult = await http.SendAsync(mapRequest);
    Check(mapResult.StatusCode == HttpStatusCode.PartialContent && (await mapResult.Content.ReadAsByteArrayAsync()).SequenceEqual("PMTiles\x03"u8.ToArray()), "Offline map range endpoint");
    File.Delete(endpointMap);
    using var send = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(new byte[] { 9 }) };
    send.Headers.Add("X-MCC-Board-Token", endpoint.Token); send.Headers.Add("Origin", endpoint.Origin);
    Check((await http.SendAsync(send)).StatusCode == HttpStatusCode.OK, "Endpoint accepts authorized write");
    var bytes = new byte[4]; await peer.GetStream().ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(bytes.SequenceEqual(new byte[] { (byte)'<', 1, 0, 9 }), "Endpoint sends framed radio packet");
    using var secondSend = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.Origin}/board/{secondId}/api/frame") { Content = new ByteArrayContent(new byte[] { 44 }) };
    secondSend.Headers.Add("X-MCC-Board-Token", endpoint.Token); secondSend.Headers.Add("Origin", endpoint.Origin);
    Check((await http.SendAsync(secondSend)).StatusCode == HttpStatusCode.OK && secondBoard.LastWrite?.SequenceEqual(new byte[] { 44 }) == true && peer.Available == 0, "Writes remain isolated to the selected board");
    secondBoard.Received.Enqueue(new byte[] { 55 });
    using var secondGet = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.Origin}/board/{secondId}/api/frame"); secondGet.Headers.Add("X-MCC-Board-Token", endpoint.Token);
    using var secondResult = await http.SendAsync(secondGet);
    Check((await secondResult.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 55 }), "Each board has an independent receive queue");
    await peer.GetStream().WriteAsync(new byte[] { (byte)'>', 1, 0, 23 });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (board.Received.PeekAfterAcknowledgement(null) is null) await Task.Delay(10, timeout.Token);
    using var get = new HttpRequestMessage(HttpMethod.Get, url); get.Headers.Add("X-MCC-Board-Token", endpoint.Token);
    using var result = await http.SendAsync(get, timeout.Token);
    Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 23 }), "Endpoint receives radio packet");
}
finally { endpointRadio.Stop(); }

Check(WifiDiscovery.Hosts("192.0.2.20/30").Select(x => x.ToString()).SequenceEqual(new[] { "192.0.2.21", "192.0.2.22" }), "IPv4 discovery host range");
Check(WifiDiscovery.SuggestedSubnet(IPAddress.Parse("192.0.3.25"), 23) == "192.0.2.0/23", "Discovery suggests the adapter's actual subnet");
Check(WifiDiscovery.SuggestedSubnet(IPAddress.Parse("10.7.8.25"), 16) == "10.7.8.0/22", "Large networks use a bounded discovery range");
Check(WifiDiscovery.SuggestedSubnet(IPAddress.Parse("192.0.2.25"), 32) == "192.0.2.25/32", "Host-only network suggestion");
await ThrowsAsync<ArgumentException>(() => Task.Run(() => WifiDiscovery.Hosts("192.0.2.0/21")));
using (var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    int port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var respond = Task.Run(async () => {
        var probe = await responder.ReceiveAsync(deadline.Token);
        Check(System.Text.Encoding.ASCII.GetString(probe.Buffer) == "MESHCORE_DISCOVER_V1", "Discovery probe");
        await responder.SendAsync("MESHCORE_V4_COMPANION_V1"u8.ToArray(), probe.RemoteEndPoint, deadline.Token);
    });
    var found = await WifiDiscovery.ScanAsync("127.0.0.1/32", port: port, responseWait: TimeSpan.FromMilliseconds(300), cancellationToken: deadline.Token);
    await respond;
    Check(found.Single().Equals(IPAddress.Loopback), "Loopback discovery response");
}
using (var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
using (var replied = new ManualResetEventSlim())
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    int port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
    var respond = Task.Run(async () => {
        var probe = await responder.ReceiveAsync(deadline.Token);
        await responder.SendAsync("MESHCORE_V4_COMPANION_V1"u8.ToArray(), probe.RemoteEndPoint, deadline.Token);
        replied.Set();
    });
    var progress = new InlineDiscoveryProgress(p => {
        if (p.Attempted == 1 && !p.Waiting) { Check(replied.Wait(TimeSpan.FromSeconds(2)), "Discovery reply arrived during the scan"); Thread.Sleep(200); }
    });
    var found = await WifiDiscovery.ScanAsync("127.0.0.1/32", progress, port, TimeSpan.Zero, deadline.Token);
    await respond;
    Check(found.Single().Equals(IPAddress.Loopback), "Discovery receives replies while probes are being sent");
}
using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
    await ThrowsAsync<OperationCanceledException>(() => WifiDiscovery.ScanAsync("127.0.0.1/32", responseWait: TimeSpan.FromSeconds(5), cancellationToken: deadline.Token));

var directory = Path.Combine(Path.GetTempPath(), "mcc-native-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string receipt = Path.Combine(directory, "startup-receipt");
    string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var receiptWriter = Task.Run(async () => { await Task.Delay(150); await StartupHealthGate.WriteAsync(receipt, nonce); });
    await StartupHealthGate.WaitAsync(receipt, nonce, TimeSpan.FromSeconds(3));
    await receiptWriter;
    await ThrowsAsync<InvalidDataException>(() => StartupHealthGate.WaitAsync(receipt, new string(nonce[0] == 'a' ? 'b' : 'a', 64), TimeSpan.FromSeconds(1)));
    await ThrowsAsync<IOException>(() => StartupHealthGate.WriteAsync(receipt, nonce));
    await ThrowsAsync<TimeoutException>(() => StartupHealthGate.WaitAsync(Path.Combine(directory, "missing-receipt"), nonce, TimeSpan.FromMilliseconds(150)));
    string processReceipt = Path.Combine(directory, "process-startup-receipt");
    using (var self = System.Diagnostics.Process.GetCurrentProcess())
    {
        var processWriter = Task.Run(async () => { await Task.Delay(150); await StartupHealthGate.WriteAsync(processReceipt, nonce); });
        await StartupHealthGate.WaitForProcessAsync(self, processReceipt, nonce, TimeSpan.FromSeconds(3));
        await processWriter;
        await ThrowsAsync<TimeoutException>(() => StartupHealthGate.WaitForProcessAsync(self, Path.Combine(directory, "process-timeout"), nonce, TimeSpan.FromMilliseconds(150)));
    }
    if (OperatingSystem.IsWindows())
    {
        var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        command.ArgumentList.Add("/c"); command.ArgumentList.Add("exit 0");
        using var exited = System.Diagnostics.Process.Start(command) ?? throw new IOException("Could not start startup-exit test.");
        await ThrowsAsync<IOException>(() => StartupHealthGate.WaitForProcessAsync(exited, Path.Combine(directory, "process-exited"), nonce, TimeSpan.FromSeconds(5)));
        await exited.WaitForExitAsync();
        await ThrowsAsync<IOException>(() => StartupHealthGate.WaitForProcessAsync(exited, processReceipt, nonce, TimeSpan.FromSeconds(5), requireRunning: true));
    }
    string crashPath = Path.Combine(directory, "logs", "crashes.log");
    new CrashLog(crashPath).Record("unhandled", new InvalidOperationException("synthetic-private-message"));
    string crashEntry = await File.ReadAllTextAsync(crashPath);
    Check(crashEntry.Contains("InvalidOperationException") && !crashEntry.Contains("synthetic-private-message"), "Crash diagnostics omit exception messages");
    string instanceRoot = Path.Combine(directory, "instance-profile");
    using (var heldInstance = NativeInstanceLease.Acquire(instanceRoot))
    {
        await ThrowsAsync<IOException>(() => Task.Run(() => NativeInstanceLease.Acquire(instanceRoot)));
        await ThrowsAsync<TimeoutException>(() => NativeInstanceLease.WaitAsync(instanceRoot, TimeSpan.FromMilliseconds(150)));
    }
    using (await NativeInstanceLease.WaitAsync(instanceRoot, TimeSpan.FromSeconds(2))) { }
    var path = Path.Combine(directory, "boards.json");
    await File.WriteAllTextAsync(path, "{\"version\":1,\"boards\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"kind\":\"wifi\",\"address\":\"192.0.2.1\",\"name\":\"Original\"}]}");
    var profiles = new BoardProfiles(path);
    Check(profiles.List().Single().Name == "Original", "Read legacy board profile");
    profiles.Rename(profiles.List()[0].Id, "Renamed");
    profiles.Add("ble", "aa:bb:cc:dd:ee:ff", "Bluetooth");
    var reloaded = new BoardProfiles(path).List();
    Check(reloaded.Count == 2 && reloaded[0].Name == "Renamed" && reloaded[1].Address == "AA:BB:CC:DD:EE:FF", "Profile persistence");
    await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => profiles.Add("wifi", "192.0.2.1", "Duplicate")));
    Check(new BoardProfiles(path).List().Count == 2, "Duplicate leaves file intact");
    profiles.Remove(reloaded[1].Id);
    Check(new BoardProfiles(path).List().Count == 1, "Saved board removal persists");
    string serialPath = Path.Combine(directory, "serial-boards.json");
    var serialProfiles = new BoardProfiles(serialPath, allowSerial: true);
    serialProfiles.Add("serial", "com7", "USB test");
    Check(new BoardProfiles(serialPath, allowSerial: true).List()[0].Address == "COM7", "Serial profile persists separately");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => new BoardProfiles(serialPath)));
    if (OperatingSystem.IsWindows())
    {
        string oldProfile = Path.Combine(directory, "old-profile");
        string leveldb = Path.Combine(oldProfile, "Default", "Local Storage", "leveldb");
        Directory.CreateDirectory(leveldb);
        await File.WriteAllTextAsync(Path.Combine(leveldb, "LOCK"), "");
        await File.WriteAllTextAsync(Path.Combine(leveldb, "data"), "synthetic messages");
        string oldData = Path.Combine(directory, "old-data"); Directory.CreateDirectory(oldData);
        File.Copy(path, Path.Combine(oldData, "boards.json"));
        await File.WriteAllTextAsync(Path.Combine(oldData, "devices.json"), "[]");
        string oldMap = Path.Combine(oldData, "offline-maps", "test.pmtiles"); Directory.CreateDirectory(Path.GetDirectoryName(oldMap)!);
        await File.WriteAllTextAsync(oldMap, "synthetic map");
        await File.WriteAllTextAsync(Path.Combine(oldData, "offline-map.json"), System.Text.Json.JsonSerializer.Serialize(new { path = oldMap }));
        string staged = Path.Combine(directory, "staged");
        await LegacyMigration.StageAsync(oldProfile, oldData, staged);
        Check(File.ReadAllText(Path.Combine(staged, "WebView", "EBWebView", "Default", "Local Storage", "leveldb", "data")) == "synthetic messages" &&
              new BoardProfiles(Path.Combine(staged, "Data", "boards.json")).List().Count == 1 &&
              File.Exists(Path.Combine(oldData, "boards.json")) && !File.Exists(Path.Combine(staged, ".mcc-migration-incomplete")), "Legacy profile and board data staged without changing originals");
        using (var mapConfig = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(staged, "Data", "offline-map.json"))))
            Check(mapConfig.RootElement.GetProperty("path").GetString() == Path.Combine(staged, "Data", "offline-maps", "test.pmtiles"), "Selected local offline map points to staged copy");
        string registry = Path.Combine(directory, "native-registry");
        NativeProfileRegistry.RequireUnselected(registry);
        NativeProfile migrated = await NativeProfileRegistry.PrepareAsync(registry, oldProfile, oldData);
        await ThrowsAsync<IOException>(() => Task.Run(() => { if (OperatingSystem.IsWindows()) NativeProfileRegistry.RequireUnselected(registry); }));
        Check(File.Exists(Path.Combine(migrated.Data, "boards.json")) && NativeProfileRegistry.Open(registry) == migrated, "Native startup selects the staged profile");
        NativeProfile reopened = await NativeProfileRegistry.PrepareAsync(registry, oldProfile, oldData);
        Check(reopened == migrated, "Subsequent starts reuse the existing native profile");
        await ThrowsAsync<InvalidDataException>(() => OperatingSystem.IsWindows() ? NativeProfileRegistry.PrepareAsync(registry, oldProfile, oldData, port: 19484) : Task.CompletedTask);
        using (var heldProfileLock = new FileStream(Path.Combine(registry, ".profile.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await ThrowsAsync<IOException>(() => OperatingSystem.IsWindows() ? NativeProfileRegistry.PrepareAsync(registry, oldProfile, oldData) : Task.CompletedTask);
        File.WriteAllText(Path.Combine(registry, "current.json"), "{\"Format\":1,\"Id\":\"../escape\"}");
        await ThrowsAsync<InvalidDataException>(() => Task.Run(() => OperatingSystem.IsWindows() ? NativeProfileRegistry.Open(registry) : null));
        Check(File.Exists(Path.Combine(oldData, "boards.json")), "Invalid native profile does not alter legacy data");
        string failedRegistry = Path.Combine(directory, "failed-registry");
        await ThrowsAsync<IOException>(() => OperatingSystem.IsWindows() ? NativeProfileRegistry.PrepareAsync(failedRegistry, oldProfile, oldData, beforeCommit: _ => Task.FromException(new IOException("synthetic recovery failure"))) : Task.CompletedTask);
        Check(!File.Exists(Path.Combine(failedRegistry, "current.json")), "A failed recovery snapshot cannot select a migrated profile");
        string freshRegistry = Path.Combine(directory, "fresh-registry");
        NativeProfile fresh = await NativeProfileRegistry.CreateEmptyAsync(freshRegistry);
        Check(NativeProfileRegistry.Open(freshRegistry) == fresh && !File.Exists(Path.Combine(fresh.Data, "boards.json")), "Explicit fresh setup creates an empty selected profile");
        await ThrowsAsync<IOException>(() => OperatingSystem.IsWindows() ? NativeProfileRegistry.CreateEmptyAsync(freshRegistry) : Task.CompletedTask);
        Check(NativeProfileRegistry.Open(freshRegistry) == fresh, "Fresh setup cannot replace an existing profile");
        await ThrowsAsync<IOException>(() => OperatingSystem.IsWindows() ? LegacyMigration.StageAsync(oldProfile, oldData, staged) : Task.CompletedTask);
        await ThrowsAsync<ArgumentException>(() => OperatingSystem.IsWindows() ? LegacyMigration.StageAsync(oldProfile, oldData, Path.Combine(oldData, "nested")) : Task.CompletedTask);
        await File.WriteAllTextAsync(Path.Combine(oldData, "boards.json"), "invalid");
        string invalidStage = Path.Combine(directory, "invalid-stage");
        await ThrowsAsync<System.Text.Json.JsonException>(() => OperatingSystem.IsWindows() ? LegacyMigration.StageAsync(oldProfile, oldData, invalidStage) : Task.CompletedTask);
        Check(File.Exists(Path.Combine(invalidStage, ".mcc-migration-incomplete")), "Incomplete migration is marked after validation failure");
    }
}
finally { Directory.Delete(directory, true); }

if (OperatingSystem.IsWindows())
{
    string key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    if (WindowsCredentials.Get("secure", key) is not null || WindowsCredentials.Get("secure", key, native: true) is not null)
        throw new IOException("Generated test credential already exists; no credentials were changed.");
    try
    {
        WindowsCredentials.Put("secure", key, "temporary-test-ø");
        Check(WindowsCredentials.Exists("secure", key) && !WindowsCredentials.Exists("repeater", key), "Credential existence can be checked without reading its value");
        Check(WindowsCredentials.Get("secure", key) == "temporary-test-ø", "Credential round trip");
        Check(WindowsCredentials.ListSaved("secure", key).TryGetValue("secure/" + key, out var foundCredential) && foundCredential == "temporary-test-ø", "Credential enumeration");
        var credentialStore = new WindowsBackupCredentials();
        Check(credentialStore.Get("secure", key) == "temporary-test-ø" && credentialStore.ListSaved("secure", key).ContainsKey("secure/" + key), "Native credential store reads the compatible Windows entry");
        var isolated = new WindowsBackupCredentials(native: true);
        Check(isolated.Get("secure", key) is null, "Native credentials do not expose the legacy entry");
        isolated.Put("secure", key, "isolated-test-secret");
        Check(isolated.Get("secure", key) == "isolated-test-secret" && credentialStore.Get("secure", key) == "temporary-test-ø", "Credential namespaces remain independent");
        Check(isolated.ListSaved("secure", key).TryGetValue("secure/" + key, out var nativeValue) && nativeValue == "isolated-test-secret", "Native credential enumeration uses portable names");
        isolated.Delete("secure", key);
        Check(isolated.Get("secure", key) is null && credentialStore.Get("secure", key) == "temporary-test-ø", "Deleting native credentials preserves legacy credentials");
        Check(WindowsCredentials.Get("repeater", key) is null, "Credential scope separation");
        string restoreRoot = Path.Combine(Path.GetTempPath(), "mcc-credential-restore-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(restoreRoot);
            byte[] backup = BackupArchive.Create(new Dictionary<string, string>(), new Dictionary<string, string> { ["secure/" + key] = "synthetic-restored-secret" }, new Dictionary<string, byte[]>());
            BackupRestore.Apply(backup, restoreRoot, isolated);
            Check(isolated.Get("secure", key) == "synthetic-restored-secret" && credentialStore.Get("secure", key) == "temporary-test-ø", "Restoring into native credentials preserves legacy credentials");
            BackupRestore.Apply(BackupEnvelope.Decrypt(BackupEnvelope.Encrypt(backup, "test password 123"), "test password 123"), restoreRoot, credentialStore);
            Check(WindowsCredentials.Get("secure", key) == "synthetic-restored-secret", "Full-backup restore writes the compatible Windows credential");
        }
        finally
        {
            string target = Path.GetFullPath(restoreRoot), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("mcc-credential-restore-test-", StringComparison.Ordinal) && Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
    finally { WindowsCredentials.Delete("secure", key, native: true); WindowsCredentials.Delete("secure", key); }
    Check(WindowsCredentials.Get("secure", key) is null, "Credential deletion");
}
{
    string keyA = new('a', 64), keyB = new('b', 64);
    var source = new MigrationCredentials(); source.Put("secure", keyA, "source-a"); source.Put("repeater", keyB, "source-b");
    var destination = new MigrationCredentials();
    CredentialNamespaceMigration.EnsureNoConflicts(source, destination);
    Check(destination.ListSaved().Count == 0, "Credential preflight does not change the destination");
    CredentialNamespaceMigration.Copy(source, destination);
    Check(destination.Get("secure", keyA) == "source-a" && destination.Get("repeater", keyB) == "source-b", "Credential copy preserves both scopes");
    destination.Delete("repeater", keyB);
    CredentialNamespaceMigration.Copy(source, destination);
    Check(destination.Get("secure", keyA) == "source-a" && destination.Get("repeater", keyB) == "source-b", "Credential copy resumes matching partial results");
    destination.Put("secure", keyA, "existing");
    try { CredentialNamespaceMigration.EnsureNoConflicts(source, destination); throw new Exception("Credential preflight accepted a conflict."); }
    catch (IOException) { }
    try { CredentialNamespaceMigration.Copy(source, destination); throw new Exception("Credential conflict was not rejected."); }
    catch (IOException) { }
    Check(destination.Get("secure", keyA) == "existing" && source.Get("secure", keyA) == "source-a", "Credential conflict leaves both stores unchanged");
    var failing = new MigrationCredentials { FailOnPut = 2 };
    try { CredentialNamespaceMigration.Copy(source, failing); throw new Exception("Credential failure was not propagated."); }
    catch (IOException) { }
    Check(failing.ListSaved().Count == 0 && source.ListSaved().Count == 2, "Partial credential writes roll back without touching the source");
}
var legacyEnvelope = Convert.FromBase64String("TUNDRlVMTDEAAQIDBAUGBwgJCgsMDQ4PAAECAwQFBgcICQoLh8OlCc6SiBe8FtFwd9DUWI7MHj4zXdyEVl+z+AhaDSlc");
Check(System.Text.Encoding.UTF8.GetString(BackupEnvelope.Decrypt(legacyEnvelope, "test password 123")) == "synthetic archive", "Decrypt Python-compatible backup envelope");
var plain = System.Text.Encoding.UTF8.GetBytes("synthetic data");
var encrypted = BackupEnvelope.Encrypt(plain, "test password 123");
Check(BackupEnvelope.Decrypt(encrypted, "test password 123").SequenceEqual(plain), "Backup envelope round trip");
await ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => Task.Run(() => BackupEnvelope.Decrypt(encrypted, "wrong password 123")));
static byte[] TestArchive(string? extraName = null)
{
    using var output = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"version\":1,\"storage\":{\"example\":\"value\"},\"credentials\":{},\"selected\":null,\"radio\":null}");
        if (extraName is not null) { using var writer = new StreamWriter(zip.CreateEntry(extraName).Open()); writer.Write("bad"); }
    }
    return output.ToArray();
}
Check(BackupArchive.Validate(TestArchive()).Manifest.GetProperty("version").GetInt32() == 1, "Validate backup archive");
await ThrowsAsync<InvalidDataException>(() => Task.Run(() => BackupArchive.Validate(TestArchive("../escape"))));
var newArchive = BackupArchive.Create(new Dictionary<string, string> { ["settings"] = "value", ["rcc6-session:temporary"] = "discard" },
    new Dictionary<string, string>(), new Dictionary<string, byte[]>());
if (Environment.GetEnvironmentVariable("MCC_TEST_BACKUP_FIXTURE") is string fixturePath && fixturePath.Length > 0)
{
    var compatibleArchive = BackupArchive.Create(new Dictionary<string, string> { ["settings"] = "value" }, new Dictionary<string, string>(), new Dictionary<string, byte[]>(), boardProfiles: "{\"version\":1,\"boards\":[]}");
    File.WriteAllBytes(fixturePath, BackupEnvelope.Encrypt(compatibleArchive, "test password 123"));
}
var archiveManifest = BackupArchive.Validate(BackupEnvelope.Decrypt(BackupEnvelope.Encrypt(newArchive, "test password 123"), "test password 123")).Manifest;
Check(archiveManifest.GetProperty("storage").GetProperty("settings").GetString() == "value" &&
    !archiveManifest.GetProperty("storage").TryGetProperty("rcc6-session:temporary", out _), "Create encrypted backup and exclude session state");
await ThrowsAsync<InvalidDataException>(() => Task.Run(() => BackupArchive.Create(new Dictionary<string, string>(),
    new Dictionary<string, string>(), new Dictionary<string, byte[]> { ["../escape"] = new byte[] { 1 } })));
string restoreDirectory = Path.Combine(Path.GetTempPath(), "mcc-restore-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(restoreDirectory);
try
{
    string credentialKey = new string('a', 64);
    string sampleMap = Path.Combine(restoreDirectory, "sample.pmtiles");
    File.WriteAllBytes(sampleMap, new byte[] { 1, 2, 3 });
    File.WriteAllText(Path.Combine(restoreDirectory, "offline-map.json"), System.Text.Json.JsonSerializer.Serialize(new { path = sampleMap }));
    var snapshot = BackupArchive.Validate(BackupSnapshot.Create(restoreDirectory, new Dictionary<string, string>(), new Dictionary<string, string>()));
    Check(snapshot.Manifest.GetProperty("selected").GetString()!.StartsWith("maps/") && snapshot.Files.Count == 1, "Snapshot includes selected map");
    byte[] samplePm = new byte[120]; "PMTiles\x03"u8.CopyTo(samplePm); samplePm[101] = 17;
    File.WriteAllBytes(sampleMap, samplePm);
    var maps = new OfflineMapStore(); maps.Select(sampleMap);
    Check(maps.Info.Available && maps.Info.MaxZoom == 17 && maps.ReadRange(0, 7)!.Value.Data.SequenceEqual("PMTiles\x03"u8.ToArray()), "Read PMTiles header and range");
    string sampleMb = Path.Combine(restoreDirectory, "sample.mbtiles");
    using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sampleMb, Pooling = false }.ToString()))
    {
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB); INSERT INTO tiles VALUES (1, 0, 1, X'89504E470D0A1A0A00')";
        cmd.ExecuteNonQuery();
    }
    maps.Select(sampleMb);
    Check(maps.ReadTile(1, 0, 0)?.ContentType == "image/png" && maps.ReadTile(1, 0, 1) is null, "Read MBTiles with flipped tile row");
    string oldBoards = "{\"version\":1,\"boards\":[]}", newBoards = "{\"version\":1,\"boards\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"kind\":\"wifi\",\"address\":\"192.0.2.1\",\"name\":\"Restored\"}]}";
    File.WriteAllText(Path.Combine(restoreDirectory, "boards.json"), newBoards);
    var savedBoards = BackupSnapshot.Create(restoreDirectory, new Dictionary<string, string>(), new Dictionary<string, string>());
    Check(BackupArchive.Validate(savedBoards).Manifest.GetProperty("boardProfiles").GetString() == newBoards, "Full backup includes saved board profiles");
    var restoreArchive = BackupArchive.Create(new Dictionary<string, string>(), new Dictionary<string, string> { ["secure/" + credentialKey] = "new" },
        new Dictionary<string, byte[]> { ["devices.json"] = System.Text.Encoding.UTF8.GetBytes("[1]") }, boardProfiles: newBoards);
    File.WriteAllText(Path.Combine(restoreDirectory, "boards.json"), oldBoards);
    File.WriteAllText(Path.Combine(restoreDirectory, "devices.json"), "[0]");
    var fake = new TestCredentials { Value = "old", FailNextPut = true };
    await ThrowsAsync<IOException>(() => Task.Run(() => BackupRestore.Apply(restoreArchive, restoreDirectory, fake)));
    Check(File.ReadAllText(Path.Combine(restoreDirectory, "devices.json")) == "[0]" && fake.Value == "old" && File.ReadAllText(Path.Combine(restoreDirectory, "boards.json")) == oldBoards, "Failed restore rolls back files, profiles and credentials");
    BackupRestore.Apply(restoreArchive, restoreDirectory, fake);
    Check(File.ReadAllText(Path.Combine(restoreDirectory, "devices.json")) == "[1]" && fake.Value == "new" && new BoardProfiles(Path.Combine(restoreDirectory, "boards.json")).List().Single().Name == "Restored", "Restore applies files, profiles and credentials");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => BackupArchive.Create(new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, byte[]>(), boardProfiles: "{}")));
}
finally { Directory.Delete(restoreDirectory, true); }
string firmwareDir = Path.Combine(Path.GetTempPath(), "mcc-firmware-test-" + Guid.NewGuid().ToString("N"));
try
{
    string targetDir = Path.Combine(firmwareDir, "v3-oled"); Directory.CreateDirectory(targetDir);
    string[] names = ["bootloader.bin", "partitions.bin", "boot_app0.bin", "firmware.bin"];
    var manifest = new Dictionary<string, string>();
    foreach (string name in names)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name); File.WriteAllBytes(Path.Combine(targetDir, name), bytes);
        manifest[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }
    File.WriteAllText(Path.Combine(firmwareDir, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["v3-oled"] = manifest }));
    Check(FirmwareCatalog.Verify(firmwareDir, "v3-oled").MinimumFlashMegabytes == 8, "Firmware manifest verification");
    int flashSize = NativeFlashPlan.ParseFlashSize("Chip type: esp32s3 (revision v0.2)\nFlash size: 8MB");
    Check(NativeFlashPlan.Create(firmwareDir, "v3-oled", "COM7", flashSize).Select(item => item.Offset).SequenceEqual(new[] { "0x0", "0x8000", "0xe000", "0x10000" }), "Flash plan preserves individual segment offsets");
    var fakeFlasher = new TestFlashTool();
    await new EspFlashExecutor(fakeFlasher).FlashAsync(firmwareDir, "v3-oled", "COM7", new[] { "COM7" });
    Check(fakeFlasher.Commands.Count(command => command == "write-bin") == 4 && fakeFlasher.Commands.Count(command => command == "checksum-md5") == 4 && fakeFlasher.Commands.Last() == "reset", "Flash sequence writes and verifies four regions");
    var failingFlasher = new TestFlashTool { CorruptVerification = true };
    await ThrowsAsync<IOException>(() => new EspFlashExecutor(failingFlasher).FlashAsync(firmwareDir, "v3-oled", "COM7", new[] { "COM7" }));
    Check(!failingFlasher.Commands.Contains("reset"), "Verification failure does not report successful flash");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => NativeFlashPlan.ParseFlashSize("Chip type: esp32s3\nCould not detect flash size, defaulting to 8MB\nFlash size: 8MB")));
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => NativeFlashPlan.Create(firmwareDir, "v3-oled", "COM7", 4)));
    File.WriteAllText(Path.Combine(targetDir, "firmware.bin"), "tampered");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => FirmwareCatalog.Verify(firmwareDir, "v3-oled")));
    FirmwareCatalog.ValidateWifiCredentials("network", "12345678");
    await ThrowsAsync<ArgumentException>(() => Task.Run(() => FirmwareCatalog.ValidateWifiCredentials("network", "short")));
    using var provisioning = new WifiProvisioning("network", "12345678");
    var command = provisioning.ProcessLine("MC_PROVISION_READY_V1");
    Check(command is not null && System.Text.Encoding.ASCII.GetString(command) == "MC_WIFI:6e6574776f726b:3132333435363738\n", "USB Wi-Fi provisioning command");
    Check(provisioning.ProcessLine("MC_PROVISION_READY_V1") is null, "Wi-Fi credentials are sent only once");
    provisioning.ProcessLine("MC_PROVISION_OK"); provisioning.ProcessLine("PC WiFi: connected at 192.0.2.3");
    Check(provisioning.Complete && provisioning.Address?.ToString() == "192.0.2.3", "Wi-Fi provisioning confirmation");
    using var provisionStream = new ProvisionTestStream("MC_PROVISION_READY_V1\nMC_PROVISION_OK\nPC WiFi: connected at 192.0.2.4\n");
    using var provisionState = new WifiProvisioning("network", "12345678");
    Check((await UsbWifiProvisioner.RunStreamAsync(provisionStream, provisionState)).ToString() == "192.0.2.4" && provisionStream.Written.Count == 1,
        "USB provisioning waits for acknowledgement and address");
}
finally { if (Directory.Exists(firmwareDir)) Directory.Delete(firmwareDir, true); }
string installRoot = Path.Combine(Path.GetTempPath(), "mcc-package-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(installRoot);
try
{
    string packagePath = Path.Combine(installRoot, "test.zip");
    void CreatePackage(string name, bool badHash)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("synthetic executable");
        string hash = badHash ? new string('0', 64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        using var file = new FileStream(packagePath, FileMode.Create, FileAccess.Write);
        using var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write(System.Text.Json.JsonSerializer.Serialize(new { version = 1, files = new Dictionary<string, string> { [name] = hash } }));
        using var output = zip.CreateEntry(name).Open(); output.Write(payload);
    }
    CreatePackage("bin/app.exe", false);
    string staged = await InstallPackage.StageAsync(packagePath, Path.Combine(installRoot, "staging"));
    Check(File.ReadAllText(Path.Combine(staged, "bin", "app.exe")) == "synthetic executable", "Installer stages verified files");
    CreatePackage("bin/app.exe", true);
    await ThrowsAsync<InvalidDataException>(() => InstallPackage.StageAsync(packagePath, Path.Combine(installRoot, "staging")));
    Check(Directory.GetDirectories(Path.Combine(installRoot, "staging")).Length == 1, "Failed installer staging is removed");
    CreatePackage("../escape", false);
    await ThrowsAsync<InvalidDataException>(() => InstallPackage.StageAsync(packagePath, Path.Combine(installRoot, "staging")));
    CreatePackage("CON.txt", false);
    await ThrowsAsync<InvalidDataException>(() => InstallPackage.StageAsync(packagePath, Path.Combine(installRoot, "staging")));
    CreatePackage("app.exe", false);
    string isolatedInstall = Path.Combine(installRoot, "isolated-install"); Directory.CreateDirectory(isolatedInstall);
    string firstVersion = await InstallPackage.StageAsync(packagePath, Path.Combine(isolatedInstall, "staging"));
    await VersionedInstall.ActivateAsync(isolatedInstall, firstVersion, "1.0.0", "app.exe");
    string currentPath = VersionedInstall.ResolveCurrent(isolatedInstall);
    Check(File.ReadAllText(currentPath) == "synthetic executable" && currentPath.Contains("1.0.0"), "Install activates verified staged version");
    string secondVersion = await InstallPackage.StageAsync(packagePath, Path.Combine(isolatedInstall, "staging"));
    await ThrowsAsync<InvalidDataException>(() => VersionedInstall.ActivateAsync(isolatedInstall, secondVersion, "../escape", "app.exe"));
    Check(VersionedInstall.ResolveCurrent(isolatedInstall) == currentPath && Directory.Exists(secondVersion), "Rejected activation preserves current version");
    await VersionedInstall.ActivateAsync(isolatedInstall, secondVersion, "1.0.1", "app.exe");
    Check(File.Exists(currentPath) && VersionedInstall.ResolveCurrent(isolatedInstall).Contains("1.0.1"), "Updating keeps the previous version intact");
    string resumeStage = await InstallPackage.StageAsync(packagePath, Path.Combine(isolatedInstall, "staging"));
    await VersionedInstall.VerifyResumeAsync(isolatedInstall, resumeStage, "1.0.1", "app.exe");
    await ThrowsAsync<IOException>(() => VersionedInstall.VerifyResumeAsync(isolatedInstall, resumeStage, "1.0.0", "app.exe"));
    await File.WriteAllTextAsync(Path.Combine(resumeStage, "app.exe"), "tampered");
    await ThrowsAsync<IOException>(() => VersionedInstall.VerifyResumeAsync(isolatedInstall, resumeStage, "1.0.1", "app.exe"));
    string updatedPath = VersionedInstall.ResolveCurrent(isolatedInstall);
    Check(!await VersionedInstall.RollbackIfUnchangedAsync(isolatedInstall, currentPath, updatedPath), "Conditional rollback rejects changed pointers");
    Check(VersionedInstall.ResolveCurrent(isolatedInstall) == updatedPath, "Rejected rollback preserves the active version");
    Check(await VersionedInstall.RollbackIfUnchangedAsync(isolatedInstall, updatedPath, currentPath), "Conditional rollback restores the previous version");
    Check(VersionedInstall.ResolveCurrent(isolatedInstall) == currentPath, "Conditional rollback restores the correct pointer");
    await VersionedInstall.RollbackAsync(isolatedInstall);
    await VersionedInstall.RollbackAsync(isolatedInstall);
    Check(VersionedInstall.ResolveCurrent(isolatedInstall) == currentPath, "Rollback restores the previous version pointer");
    await VersionedInstall.RollbackAsync(isolatedInstall);
    Check(VersionedInstall.ResolveCurrent(isolatedInstall).Contains("1.0.1"), "Rollback retains the version it replaced");
    string thirdVersion = await InstallPackage.StageAsync(packagePath, Path.Combine(isolatedInstall, "staging"));
    using (var heldInstallLock = new FileStream(Path.Combine(isolatedInstall, ".install.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        await ThrowsAsync<IOException>(() => VersionedInstall.ActivateAsync(isolatedInstall, thirdVersion, "1.0.2", "app.exe"));
    Check(Directory.Exists(thirdVersion) && VersionedInstall.ResolveCurrent(isolatedInstall).Contains("1.0.1"), "Concurrent install is rejected without changing the current version");
    if (OperatingSystem.IsWindows())
    {
        using (var heldPrevious = new FileStream(Path.Combine(isolatedInstall, "previous.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await ThrowsAsync<UnauthorizedAccessException>(() => VersionedInstall.ActivateAsync(isolatedInstall, thirdVersion, "1.0.2", "app.exe"));
        Check(Directory.Exists(thirdVersion) && !Directory.Exists(Path.Combine(isolatedInstall, "versions", "1.0.2")) && VersionedInstall.ResolveCurrent(isolatedInstall).Contains("1.0.1"), "Failed pointer update returns staged files and preserves the running version");
    }
    File.WriteAllText(Path.Combine(isolatedInstall, "current.json"), "{\"Format\":1,\"Version\":\"../escape\",\"Executable\":\"app.exe\"}");
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => VersionedInstall.ResolveCurrent(isolatedInstall)));
    Check(VersionedInstall.CanRecoverPrevious(isolatedInstall), "Previous version remains available after a damaged pointer");
    await VersionedInstall.RecoverPreviousAsync(isolatedInstall);
    Check(VersionedInstall.ResolveCurrent(isolatedInstall) == currentPath, "Recover previous version when current pointer is damaged");
}
finally { Directory.Delete(installRoot, true); }
if (OperatingSystem.IsWindows())
{
    string root = Path.Combine(Path.GetTempPath(), "mcc-profile-test-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "profile"), destination = Path.Combine(root, "snapshot");
    try
    {
        string leveldb = Path.Combine(source, "Default", "Local Storage", "leveldb"); Directory.CreateDirectory(leveldb);
        File.WriteAllText(Path.Combine(leveldb, "LOCK"), ""); File.WriteAllText(Path.Combine(leveldb, "data"), "synthetic history");
        await ProfileSnapshot.CopyAsync(source, destination);
        Check(File.ReadAllText(Path.Combine(destination, "Default", "Local Storage", "leveldb", "data")) == "synthetic history" && !File.Exists(Path.Combine(destination, ".mcc-incomplete")), "Profile snapshot copies and verifies data");
        using var held = new FileStream(Path.Combine(leveldb, "LOCK"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await ThrowsAsync<IOException>(() => OperatingSystem.IsWindows() ? ProfileSnapshot.CopyAsync(source, Path.Combine(root, "blocked")) : Task.CompletedTask);
        Check(!Directory.Exists(Path.Combine(root, "blocked")), "Locked profile is not copied");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
string releaseJson = "[{\"tag_name\":\"v2.0.40\",\"draft\":false,\"published_at\":\"2026-01-01\",\"assets\":[{\"name\":\"MeshCore-Command-Native-Windows-2.0.40-Setup.exe\",\"state\":\"uploaded\",\"size\":123,\"digest\":\"sha256:" + new string('a', 64) + "\",\"browser_download_url\":\"https://github.com/Nortag82/MeshCore-Command/releases/download/v2.0.40/MeshCore-Command-Native-Windows-2.0.40-Setup.exe\"}]}]";
using (var releaseDoc = System.Text.Json.JsonDocument.Parse(releaseJson))
{
    Check(ReleaseSelector.Select(releaseDoc.RootElement, "2.0.39")?.Version == "2.0.40", "Select trusted installer release");
    Check(ReleaseSelector.Select(releaseDoc.RootElement, "2.0.40") is null, "Ignore installed release");
}
using (var oldHostRelease = System.Text.Json.JsonDocument.Parse(releaseJson.Replace("MeshCore-Command-Native-Windows", "MeshCore-Command-Windows")))
    Check(ReleaseSelector.Select(oldHostRelease.RootElement, "2.0.39") is null, "The native updater ignores Python-host installers");
using (var mixed = System.Text.Json.JsonDocument.Parse("[" + releaseJson[1..^1] + ",{\"tag_name\":\"v2.0.41\",\"published_at\":\"2026-01-02\",\"assets\":[]}]"))
    Check(ReleaseSelector.Select(mixed.RootElement, "2.0.39")?.Version == "2.0.40", "Unrelated newer releases do not hide a native update");
using (var badRelease = System.Text.Json.JsonDocument.Parse(releaseJson.Replace("https://github.com/", "https://example.com/")))
    await ThrowsAsync<InvalidDataException>(() => Task.Run(() => ReleaseSelector.Select(badRelease.RootElement, "2.0.39")));
string updateDir = Path.Combine(Path.GetTempPath(), "mcc-updater-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(updateDir);
try
{
    using var updateHttp = new HttpClient(new UpdaterTestHandler());
    var updater = new UpdaterClient(updateHttp);
    string installer = await updater.DownloadAsync("2.0.39", "2.0.40", updateDir);
    Check(File.ReadAllText(installer) == "installer fixture", "Updater validates and saves installer");
    using var badHttp = new HttpClient(new UpdaterTestHandler(true));
    await ThrowsAsync<InvalidDataException>(() => new UpdaterClient(badHttp).DownloadAsync("2.0.39", "2.0.40", updateDir));
    using var corruptHttp = new HttpClient(new UpdaterTestHandler(corrupt: true));
    await ThrowsAsync<InvalidDataException>(() => new UpdaterClient(corruptHttp).DownloadAsync("2.0.39", "2.0.40", updateDir));
    Check(Directory.GetDirectories(updateDir).Length == 1, "Rejected installers are removed");
    var handoff = await UpdateHandoff.PrepareAsync(updater, "2.0.39", "2.0.40", updateDir, 1234, 5678);
    Check(File.ReadAllText(handoff.FileName) == "installer fixture" && handoff.ArgumentList.SequenceEqual(new[] { "--native-update", "--parent-pid=1234", "--parent-start=5678", "--expected-version=2.0.40" }), "Updater handoff carries the verified installer and exact parent identity");
    await ThrowsAsync<ArgumentException>(() => UpdateHandoff.PrepareAsync(updater, "2.0.39", "../escape", updateDir, 1234, 5678));
}
finally { Directory.Delete(updateDir, true); }
var position = NmeaPosition.Parse("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A");
Check(position is not null && Math.Abs(position.Latitude - 48.1173) < 0.000001 && Math.Abs(position.Longitude - 11.5166666667) < 0.000001, "GPS RMC fix");
Check(NmeaPosition.Parse("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*00") is null, "GPS checksum rejection");
using (var gps = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("$bad*00\r\n$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A\r\n")))
    Check(Math.Abs((await UsbGps.ReadFromStreamAsync(gps)).Latitude - 48.1173) < 0.000001, "GPS stream ignores invalid data before fix");
using (var elevationHttp = new HttpClient(new ElevationTestHandler()))
{
    var elevation = new ElevationService(elevationHttp);
    var heights = await elevation.QueryAsync(System.Text.Encoding.UTF8.GetBytes("[[56.0,-4.0],[55.0,-3.0]]"));
    Check(heights.SequenceEqual(new double[] { 10, 20 }), "Elevation response validated");
    await ThrowsAsync<ArgumentException>(() => elevation.QueryAsync(System.Text.Encoding.UTF8.GetBytes("[[91,0]]")));
}
string downloadsDir = Path.Combine(Path.GetTempPath(), "mcc-download-test-" + Guid.NewGuid().ToString("N"));
using (var fakeHttp = new HttpClient(new BogusHelperHandler()))
{
    await using var downloads = new OfflineDownloads(fakeHttp, downloadsDir, new OfflineMapStore(), _ => throw new Exception("Must not select an unverified map"));
    await ThrowsAsync<ArgumentException>(() => Task.Run(() => downloads.Start(new double[] { -30, -80, 30, 80 }, 10)));
    downloads.Start(new double[] { -5, 55, -4, 56 }, 10);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (downloads.Status.Status == "running") await Task.Delay(10, timeout.Token);
    Check(downloads.Status.Status == "error" && downloads.Status.Message.Contains("integrity"), "Unverified map helper is rejected");
}
if (Directory.Exists(downloadsDir)) Directory.Delete(downloadsDir, true);
Console.WriteLine("All native core compatibility checks passed.");

sealed class MigrationCredentials : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new();
    private int _puts;
    public int FailOnPut { get; init; }
    public string? Get(string scope, string key) => _values.GetValueOrDefault(scope + "/" + key);
    public void Put(string scope, string key, string value) { _values[scope + "/" + key] = value; if (++_puts == FailOnPut) throw new IOException("Synthetic failure after writing."); }
    public void Delete(string scope, string key) => _values.Remove(scope + "/" + key);
    public IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null) =>
        _values.Where(item => scope is null || item.Key == scope + "/" + key).ToDictionary(item => item.Key, item => item.Value);
}

sealed class TestCredentials : IBackupCredentials
{
    public string? Value;
    public bool FailNextPut;
    public string? Get(string scope, string key) => Value;
    public void Put(string scope, string key, string value)
    {
        Value = value;
        if (FailNextPut) { FailNextPut = false; throw new IOException("Simulated write failure"); }
    }
    public void Delete(string scope, string key) => Value = null;
}

sealed class IdentityTestBoard : ICompanionBoard
{
    public FrameQueue Received { get; } = new();
    public bool Connected => true;
    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Span[0] == 20) { Received.Enqueue(new byte[] { 12, 0xa0, 0x0f }); return Task.CompletedTask; }
        if (payload.Span[0] != 1) throw new Exception("Unexpected identity request");
        var frame = new byte[36]; frame[0] = 5; frame[4] = 12; Received.Enqueue(frame);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class RecordingBoard : ICompanionBoard
{
    public FrameQueue Received { get; } = new();
    public bool Connected => true;
    public byte[]? LastWrite { get; private set; }
    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) { LastWrite = payload.ToArray(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class TestBleTransport : IBlePacketTransport
{
    public event Action<byte[]>? PacketReceived;
    public event Action? Disconnected;
    public byte[]? LastWrite;
    public Task WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) { LastWrite = packet.ToArray(); return Task.CompletedTask; }
    public void Receive(byte[] packet) => PacketReceived?.Invoke(packet);
    public ValueTask DisposeAsync() { Disconnected?.Invoke(); return ValueTask.CompletedTask; }
}

sealed class ElevationTestHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "api.open-meteo.com") throw new Exception("Unexpected elevation host");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"elevation\":[10,20]}") });
    }
}

sealed class BogusHelperHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "github.com") throw new Exception("Unexpected helper host");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid helper") });
    }
}

sealed class UpdaterTestHandler(bool badRedirect = false, bool corrupt = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "api.github.com")
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes("installer fixture");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            string json = "[{\"tag_name\":\"v2.0.40\",\"published_at\":\"2026-01-01\",\"assets\":[{\"name\":\"MeshCore-Command-Native-Windows-2.0.40-Setup.exe\",\"state\":\"uploaded\",\"size\":" + data.Length + ",\"digest\":\"sha256:" + hash + "\",\"browser_download_url\":\"https://github.com/Nortag82/MeshCore-Command/releases/download/v2.0.40/MeshCore-Command-Native-Windows-2.0.40-Setup.exe\"}]}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
        if (badRedirect)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://example.com/bad"); return Task.FromResult(redirect);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corrupt ? "corrupted fixture" : "installer fixture") });
    }
}

sealed class TestFlashTool : IFlashTool
{
    public List<string> Commands { get; } = new();
    public bool CorruptVerification;
    private readonly Dictionary<string, string> _written = new();
    public Task<string> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Commands.Add(args[0]);
        if (args[0] == "board-info") return Task.FromResult("Chip type: esp32s3\nFlash size: 8MB");
        if (args[0] == "write-bin") { _written[args[^2]] = args[^1]; return Task.FromResult(""); }
        if (args[0] == "checksum-md5")
        {
            string hash = CorruptVerification ? new string('0', 32) : Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(_written[args[^2]])));
            return Task.FromResult("0x" + hash + "\n");
        }
        return Task.FromResult("");
    }
}

sealed class InlineDiscoveryProgress(Action<DiscoveryProgress> report) : IProgress<DiscoveryProgress>
{
    public void Report(DiscoveryProgress value) => report(value);
}

sealed class ProvisionTestStream(string input) : Stream
{
    private readonly MemoryStream _input = new(System.Text.Encoding.ASCII.GetBytes(input));
    public List<byte[]> Written { get; } = new();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => Written.Add(buffer.AsSpan(offset, count).ToArray());
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Written.Add(buffer.ToArray()); return ValueTask.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
}
