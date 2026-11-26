using MeshCore.Windows.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class HfLinkChecks
{
    public static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("mcc-hflink-test-");
        try
        {
            string path = Path.Combine(directory.FullName, "hf.sqlite3");
            var link = new HfLink(path);
            CheckMercurySettings(link, directory.FullName);
            Check(link.Station().UseBundledMercury, "New stations default to bundled Mercury");
            Check(!link.FirewallSetupOffered(), "An unopened profile has no firewall prompt record");
            link.MarkFirewallSetupOffered();
            Check(new HfLink(path).FirewallSetupOffered(), "The first firewall prompt is recorded across restarts");
            link.SaveStation(new HfStation("M0ABC", Host: "example.org", ControlPort: 10300, DataPort: 10301, UseBundledMercury: false));
            Check(new HfLink(path).Station() is { UseBundledMercury: false, Host: "example.org" }, "An explicit external modem remains selected");
            link.SaveStation(new HfStation("M0ABC", Host: "example.org", ControlPort: 10300, DataPort: 10301, UseBundledMercury: true));
            Check(link.Station() is { Host: "127.0.0.1", ControlPort: 8300, DataPort: 8301 }, "Bundled Mercury is forced to the local ports");
            var id = link.Queue("G1XYZ", "First message");
            Check(link.Messages().Single().Status == HfStatus.AwaitingApproval, "Queue must await approval");
            Check(await link.ApproveAndSendAsync(id, HfSimulation.Disconnected, false, default) == HfStatus.Failed, "Mock disconnect");
            link.Retry(id);
            Check(link.Messages().Single().Status == HfStatus.AwaitingApproval, "Retry requires renewed approval");
            Check(await link.ApproveAndSendAsync(id, HfSimulation.Timeout, false, default) == HfStatus.Failed, "Mock timeout");
            link.Retry(id);
            Check(await link.ApproveAndSendAsync(id, HfSimulation.NoAcknowledgement, false, default) == HfStatus.Unconfirmed,
                "Local send does not confirm delivery");
            link.Retry(id);
            Check(await link.ApproveAndSendAsync(id, HfSimulation.Acknowledged, false, default) == HfStatus.Delivered,
                "Matching app receipt confirms delivery");
            var cancelled = link.Queue("G1XYZ", "Cancel this");
            link.Cancel(cancelled);
            Check(link.Messages().Single(m => m.Id == cancelled).Status == HfStatus.Cancelled, "Cancellation persists");
            int prompts = 0, notifications = 0;
            await link.ReceiveAsync(false, (_, _) => { prompts++; return Task.FromResult(true); }, default, message =>
            {
                notifications++;
                Check(link.Messages().Any(saved => saved.Id == message.Id && saved.Incoming), "Notification follows storage");
            });
            Check(prompts == 1 && notifications == 1 && link.Messages().Count(m => m.Incoming) == 1, "Duplicate incoming suppression");

            var reopened = new HfLink(path);
            Check(reopened.Messages().Any(m => m.Id == id && m.Status == HfStatus.Delivered), "History survives restart");
            var audit = reopened.Audit();
            Check(audit.Any(a => a.Message == id && a.Actor == "local operator" && a.Event.Contains("Approved")) &&
                audit.All(a => !a.Event.Contains("First message")), "Approval and transmission audit contains no message text");
            reopened.DeleteMessage(reopened.Messages().Single(message => message.Id == id));
            Check(new HfLink(path).Messages().All(message => message.Id != id) &&
                reopened.Messages().Any(message => message.Id == cancelled), "Deleting a sent message preserves other messages");
            await CheckDeletedReceiveAsync(Path.Combine(directory.FullName, "backup-source", "hflink-native.sqlite3"));

            await LoopbackAsync(wrongReceipt: false);
            await LoopbackAsync(wrongReceipt: true);
            await ReceiveLoopbackAsync();
        }
        finally { directory.Delete(recursive: true); }
        Console.WriteLine("HFLink SQLite, bundled station settings, simulation and Mercury loopback checks passed.");
    }

    private static async Task CheckDeletedReceiveAsync(string path)
    {
        var packet = new HfPacket(HfPacket.Version, "message", Guid.NewGuid(), Guid.NewGuid(), "G1XYZ", "M0ABC", "Received example");
        var modem = new ReplayModem(packet);
        var link = new HfLink(path, (_, _) => modem);
        link.SaveStation(new HfStation("M0ABC"));
        int notifications = 0;
        await link.ReceiveAsync(false, (_, _) => Task.FromResult(false), default, _ => notifications++);
        link.DeleteMessage(link.Messages().Single());
        var reopened = new HfLink(path, (_, _) => new ReplayModem(packet with { Attempt = Guid.NewGuid() }));
        await reopened.ReceiveAsync(false, (_, _) => throw new InvalidOperationException("Deleted message should not prompt for a receipt."),
            default, _ => notifications++);
        Check(reopened.Messages().Count == 0 && notifications == 1, "The sender's retry does not restore a deleted message");
        Check(reopened.Audit().Any(item => item.Event == "Received message deleted" && item.Message == packet.Id),
            "A deletion records metadata in the audit log");

        string root = Path.GetDirectoryName(path)!;
        var next = packet with { Id = Guid.NewGuid(), Attempt = Guid.NewGuid() };
        var source = new HfLink(path, (_, _) => new ReplayModem(next));
        await source.ReceiveAsync(false, (_, _) => Task.FromResult(false), default);
        var outgoing = source.Queue("G1XYZ", "Outgoing example");
        source.MarkFirewallSetupOffered();
        string bundled = Path.Combine(root, "mercury", "mercury.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        const string config = "[ptt]\r\nmethod = serial\r\ndevice = COM5\r\n";
        File.WriteAllText(bundled, config);
        string external = Path.Combine(root, "external.ini");
        File.WriteAllText(external, "[main]\r\ninput_device = example\r\n");
        source.SaveMercuryConfig(MercurySettingsFile.Open(external));
        byte[] archive = BackupSnapshot.Create(root, new Dictionary<string, string> { ["message-storage"] = "MeshCore history" },
            new Dictionary<string, string>());
        byte[] encrypted = BackupEnvelope.Encrypt(archive, "synthetic password");
        string destination = Path.Combine(root, "restored");
        BackupRestore.Apply(BackupEnvelope.Decrypt(encrypted, "synthetic password"), destination, new TestCredentials());
        var restored = new HfLink(Path.Combine(destination, "hflink-native.sqlite3"), (_, _) => new ReplayModem(packet));
        Check(restored.Station().Callsign == "M0ABC" && restored.Messages().Any(message => message.Id == outgoing) &&
            restored.Messages().Any(message => message.Id == next.Id && message.Incoming) &&
            !restored.FirewallSetupOffered(), "Portable backup restores the station and both directions of history");
        Check(File.ReadAllText(Path.Combine(destination, "mercury", "mercury.ini")) == config &&
            restored.MercuryConfigPath() == Path.Combine(destination, "mercury", "external-mercury.ini") &&
            File.ReadAllText(restored.MercuryConfigPath()!) == File.ReadAllText(external), "Portable backup restores both Mercury configurations");
        await restored.ReceiveAsync(false, (_, _) => throw new InvalidOperationException("A restored deletion was not preserved."), default);
    }

    private sealed class ReplayModem(HfPacket packet) : IHfModem
    {
        public Task ProbeAsync(HfStation station, CancellationToken stop) => throw new NotSupportedException();
        public Task<HfPacket?> SendAsync(HfStation station, HfPacket message, Action accepted, CancellationToken stop) =>
            throw new NotSupportedException();
        public async Task ReceiveAsync(HfStation station, Func<HfPacket, CancellationToken, Task<HfPacket?>> onPacket, CancellationToken stop) =>
            _ = await onPacket(packet, stop);
    }

    private static void CheckMercurySettings(HfLink link, string directory)
    {
        string path = Path.Combine(directory, "mercury.ini");
        string original = "; Mercury\r\n[main]\r\ninput_device = \"USB #1\" ; keep this comment\r\n[ptt]\r\nmethod = none\r\n[custom]\r\nkey = keep\r\n";
        File.WriteAllText(path, original);
        var settings = MercurySettingsFile.Open(path);
        Check(settings.Get("main", "input_device") == "USB #1", "Reads quoted device IDs with inline comments");
        settings.Set("main", "input_device", "USB #2");
        settings.Set("ptt", "method", "serial");
        settings.Set("ptt", "device", "COM5");
        settings.Set("audio", "tx_delay_ms", "200");
        string? backup = link.SaveMercuryConfig(settings);
        Check(backup is not null && File.ReadAllText(backup) == original, "Mercury settings writes recoverable backup");
        string saved = File.ReadAllText(path);
        Check(saved.Contains("input_device = \"USB #2\" ; keep this comment\r\n") &&
              saved.Contains("method = serial\r\ndevice = COM5\r\n[custom]\r\nkey = keep\r\n") &&
              saved.Contains("[audio]\r\ntx_delay_ms = 200\r\n"), "Only selected Mercury settings change");
        Check(link.MercuryConfigPath() == path, "Remembers the Mercury settings file");
        Check(MercurySettingsFile.Open(path).Save() is null, "Unchanged config is left alone");

        var stale = MercurySettingsFile.Open(path);
        stale.Set("ptt", "device", "COM7");
        File.AppendAllText(path, "; changed by Mercury\r\n");
        try { stale.Save(); throw new InvalidOperationException("Expected a config conflict."); }
        catch (IOException) { }
        Check(File.ReadAllText(path).EndsWith("; changed by Mercury\r\n"), "Concurrent edits are preserved");
        try { stale.Set("ptt", "device", "COM7\nmethod=none"); throw new InvalidOperationException("Expected invalid device rejection."); }
        catch (ArgumentException) { }
    }

    private static async Task LoopbackAsync(bool wrongReceipt)
    {
        using var control = new TcpListener(IPAddress.Loopback, 0);
        using var data = new TcpListener(IPAddress.Loopback, 0);
        control.Start();
        data.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = deadline.Token;
        var station = new HfStation("M0ABC", ControlPort: ((IPEndPoint)control.LocalEndpoint).Port,
            DataPort: ((IPEndPoint)data.LocalEndpoint).Port, Simulation: false);
        var requested = new HfPacket(HfPacket.Version, "message", Guid.NewGuid(), Guid.NewGuid(), station.Callsign, "G1XYZ", "Loopback");
        var server = Task.Run(async () =>
        {
            using var ctl = await control.AcceptTcpClientAsync(stop);
            using var payload = await data.AcceptTcpClientAsync(stop);
            var c = ctl.GetStream();
            var d = payload.GetStream();
            await InitializeAsync(c, stop);
            Check(await ReadAsync(c, '\r', stop) == "CONNECT M0ABC G1XYZ", "Mercury CONNECT ordering");
            await WriteAsync(c, "OK\rCONNECTED M0ABC G1XYZ 2300\r", stop);
            var actual = JsonSerializer.Deserialize<HfPacket>(await ReadAsync(d, '\n', stop), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(actual == requested, "Mercury data socket payload");
            var receipt = requested.Acknowledge();
            if (wrongReceipt) receipt = receipt with { Attempt = Guid.NewGuid() };
            await WriteAsync(d, JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", stop);
            Check(await ReadAsync(c, '\r', stop) == "DISCONNECT", "Mercury session shutdown");
            await WriteAsync(c, "OK\rDISCONNECTED\r", stop);
        }, stop);
        bool accepted = false;
        var result = await new MercuryHfModem().SendAsync(station, requested, () => accepted = true, stop);
        await server;
        Check(accepted && result?.Confirms(requested) == !wrongReceipt, "Receipt must correlate with the current send attempt");
    }

    private static async Task ReceiveLoopbackAsync()
    {
        using var control = new TcpListener(IPAddress.Loopback, 0);
        using var data = new TcpListener(IPAddress.Loopback, 0);
        control.Start();
        data.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = deadline.Token;
        var station = new HfStation("M0ABC", ControlPort: ((IPEndPoint)control.LocalEndpoint).Port,
            DataPort: ((IPEndPoint)data.LocalEndpoint).Port, Simulation: false);
        var incoming = new HfPacket(HfPacket.Version, "message", Guid.NewGuid(), Guid.NewGuid(), "G1XYZ", station.Callsign, "Incoming");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var server = Task.Run(async () =>
        {
            using var ctl = await control.AcceptTcpClientAsync(stop);
            using var payload = await data.AcceptTcpClientAsync(stop);
            var c = ctl.GetStream();
            var d = payload.GetStream();
            await InitializeAsync(c, stop);
            Check(await ReadAsync(c, '\r', stop) == "LISTEN ON", "Attended receive enables listening");
            await WriteAsync(c, "OK\rPENDING\rCONNECTED G1XYZ M0ABC 500\r", stop);
            await WriteAsync(d, JsonSerializer.Serialize(incoming, json) + "\n", stop);
            var receipt = JsonSerializer.Deserialize<HfPacket>(await ReadAsync(d, '\n', stop), json);
            Check(receipt?.Confirms(incoming) == true, "Receipt returns over the established session");
            Check(await ReadAsync(c, '\r', stop) == "DISCONNECT", "Receiver drains the receipt");
            await WriteAsync(c, "OK\rDISCONNECTED\r", stop);
            Check(await ReadAsync(c, '\r', stop) == "LISTEN OFF", "Receiver disables listening");
            await WriteAsync(c, "OK\r", stop);
        }, stop);
        await new MercuryHfModem().ReceiveAsync(station, (packet, _) =>
        {
            Check(packet == incoming, "Incoming Mercury packet");
            return Task.FromResult<HfPacket?>(packet.Acknowledge());
        }, stop);
        await server;
    }

    private static async Task InitializeAsync(NetworkStream stream, CancellationToken stop)
    {
        foreach (string command in new[] { "LISTEN OFF", "PUBLIC OFF", "MYCALL M0ABC" })
        {
            Check(await ReadAsync(stream, '\r', stop) == command, "Mercury initialisation: " + command);
            await WriteAsync(stream, command.StartsWith("MYCALL") ? "OK\rREGISTERED M0ABC\r" : "OK\r", stop);
        }
    }

    private static async Task<string> ReadAsync(Stream stream, char end, CancellationToken stop)
    {
        var buffer = new byte[1];
        using var result = new MemoryStream();
        while (await stream.ReadAsync(buffer, stop) == 1)
        {
            if (buffer[0] == end) return Encoding.UTF8.GetString(result.ToArray());
            result.WriteByte(buffer[0]);
        }
        throw new EndOfStreamException();
    }

    private static ValueTask WriteAsync(Stream stream, string text, CancellationToken stop) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(text), stop);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
