using MeshCore.Windows.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class HfLinkChecks
{
    private const string Password = "hflink-test-password";

    public static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("mcc-hflink-test-");
        try
        {
            string path = Path.Combine(directory.FullName, "hf.sqlite3");
            var link = new HfLink(path);
            var admin = link.SetupAdministrator(Password);
            var station = new HfStation("M0ABC");
            link.SaveStation(admin, station);
            var requester = new HfUser("alice", HfRole.User, "M0AAA", true, true, "Checked by the test admin");
            link.SaveUser(admin, requester, Password);
            link.SaveUser(admin, new("operator", HfRole.Operator, "M0ABC", true, false, ""), Password);
            link.SaveUser(admin, new("denied", HfRole.User, "M0ZZZ", false, false, ""), Password);
            var user = link.SignIn("alice", Password);
            var op = link.SignIn("operator", Password);
            var denied = link.SignIn("denied", Password);
            await DeniedAsync(() => Task.FromResult(link.Queue(denied, "G1XYZ", "Unauthorized")));
            await DeniedAsync(() => Task.FromResult(link.Queue(admin, "G1XYZ", "Administrator without request access")));
            await DeniedAsync(() => Task.FromResult(link.SignIn("operator", "wrong")));
            var id = link.Queue(user, "G1XYZ", "First message");
            Check(link.Messages(user).Single().Status == HfStatus.AwaitingApproval, "Queue must await approval");
            await DeniedAsync(() => link.ApproveAndSendAsync(user, id, HfSimulation.Acknowledged, false, default));
            link.SaveUser(admin, requester with { Access = false }, "");
            await DeniedAsync(() => link.ApproveAndSendAsync(op, id, HfSimulation.Acknowledged, false, default));
            link.SaveUser(admin, requester, "");
            Check(await link.ApproveAndSendAsync(op, id, HfSimulation.Disconnected, false, default) == HfStatus.Failed, "Mock disconnect");
            link.Retry(user, id);
            Check(link.Messages(user).Single().Status == HfStatus.AwaitingApproval, "Retry requires renewed approval");
            Check(await link.ApproveAndSendAsync(op, id, HfSimulation.Timeout, false, default) == HfStatus.Failed, "Mock timeout");
            link.Retry(user, id);
            Check(await link.ApproveAndSendAsync(op, id, HfSimulation.NoAcknowledgement, false, default) == HfStatus.Unconfirmed,
                "Local send does not confirm delivery");
            link.Retry(user, id);
            Check(await link.ApproveAndSendAsync(op, id, HfSimulation.Acknowledged, false, default) == HfStatus.Delivered,
                "Matching app receipt confirms delivery");
            var cancelled = link.Queue(user, "G1XYZ", "Cancel this");
            link.Cancel(user, cancelled);
            Check(link.Messages(user).Single(m => m.Id == cancelled).Status == HfStatus.Cancelled, "Cancellation persists");
            int prompts = 0;
            await link.ReceiveAsync(op, false, (_, _) => { prompts++; return Task.FromResult(true); }, default);
            Check(prompts == 1 && link.Messages(op).Count(m => m.Incoming) == 1, "Duplicate incoming suppression");
            Check(link.Messages(user).All(m => !m.Incoming), "Ordinary users only see their own messages");

            var reopened = new HfLink(path);
            var reopenedOperator = reopened.SignIn("operator", Password);
            Check(reopened.Messages(reopenedOperator).Any(m => m.Id == id && m.Status == HfStatus.Delivered), "History survives restart");
            var audit = reopened.Audit(reopenedOperator);
            Check(audit.Any(a => a.Message == id && a.Actor == "operator" && a.Event.Contains("Approved")) &&
                audit.All(a => !a.Event.Contains("First message")), "Approval and transmission audit contains no message text");

            await LoopbackAsync(wrongReceipt: false);
            await LoopbackAsync(wrongReceipt: true);
            await ReceiveLoopbackAsync();
        }
        finally { directory.Delete(recursive: true); }
        Console.WriteLine("HFLink SQLite, permissions, simulation and Mercury loopback checks passed.");
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

    private static async Task DeniedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected HFLink access denial.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
