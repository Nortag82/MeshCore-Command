using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record HfStation(string Callsign = "", string Host = "127.0.0.1",
    int ControlPort = 8300, int DataPort = 8301, int TimeoutSeconds = 120, bool Simulation = true,
    bool UseBundledMercury = true)
{
    public static string Call(string value)
    {
        string call = value.Trim().ToUpperInvariant();
        if (call.Length > 15 || !Regex.IsMatch(call, @"\A[A-Z0-9]+(?:[/-][A-Z0-9]+)*\z") ||
            !call.Any(char.IsAsciiDigit) || !call.Any(char.IsAsciiLetter))
            throw new ArgumentException("Enter an amateur callsign of up to 15 characters.");
        return call;
    }
}

public sealed record HfPacket(string Protocol, string Type, Guid Id, Guid Attempt, string From, string To, string? Text)
{
    public const int TextLimit = 512;
    public const int WireLimit = 4096;
    public const string Version = "HFLINK/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void ValidateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > TextLimit ||
            text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Enter 1–512 UTF-8 bytes of readable text.");
    }

    internal byte[] Encode() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json) + "\n");

    internal static HfPacket Decode(byte[] line, string peer, string local)
    {
        try
        {
            var packet = JsonSerializer.Deserialize<HfPacket>(line, Json);
            if (packet is null || packet.Protocol != Version || packet.Id == Guid.Empty || packet.Attempt == Guid.Empty ||
                packet.From != peer || packet.To != local || packet.Type is not ("message" or "ack") ||
                (packet.Type == "ack" && packet.Text is not null))
                throw new InvalidDataException("Unexpected HFLink packet or station address.");
            if (packet.Type == "message") ValidateText(packet.Text);
            return packet;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Invalid HFLink data.");
        }
    }

    public HfPacket Acknowledge() => new(Version, "ack", Id, Attempt, To, From, null);

    public bool Confirms(HfPacket message) => Type == "ack" && Protocol == Version && Text is null &&
        Id == message.Id && Attempt == message.Attempt && From == message.To && To == message.From;
}

public interface IHfModem
{
    Task ProbeAsync(HfStation station, CancellationToken stop);
    Task<HfPacket?> SendAsync(HfStation station, HfPacket packet, Action accepted, CancellationToken stop);
    Task ReceiveAsync(HfStation station, Func<HfPacket, CancellationToken, Task<HfPacket?>> onPacket, CancellationToken stop);
}

public enum HfSimulation { NoAcknowledgement, Acknowledged, Disconnected, Timeout }

public sealed record HfModemStatus(string? Phase = null, string? Peer = null, bool? Keyed = null,
    bool? Busy = null, double? Snr = null, int? Bitrate = null, int? Bandwidth = null, int? QueuedBytes = null);

public sealed class MockHfModem(HfSimulation outcome = HfSimulation.NoAcknowledgement, Action<HfModemStatus>? report = null) : IHfModem
{
    public Task ProbeAsync(HfStation station, CancellationToken stop) => Task.CompletedTask;

    public async Task<HfPacket?> SendAsync(HfStation station, HfPacket packet, Action accepted, CancellationToken stop)
    {
        report?.Invoke(new("Calling (simulation)", Peer: packet.To));
        await Task.Delay(200, stop);
        if (outcome == HfSimulation.Disconnected) throw new IOException("Simulated modem disconnected.");
        if (outcome == HfSimulation.Timeout) throw new TimeoutException("Simulated Mercury session timed out.");
        accepted();
        report?.Invoke(new("Waiting for simulated receipt", Peer: packet.To));
        await Task.Delay(250, stop);
        return outcome == HfSimulation.Acknowledged ? packet.Acknowledge() : null;
    }

    public async Task ReceiveAsync(HfStation station, Func<HfPacket, CancellationToken, Task<HfPacket?>> onPacket, CancellationToken stop)
    {
        report?.Invoke(new("Listening (simulation)"));
        await Task.Delay(TimeSpan.FromSeconds(2), stop);
        string peer = station.Callsign == "N0SIM" ? "N1SIM" : "N0SIM";
        var packet = new HfPacket(HfPacket.Version, "message", Guid.NewGuid(), Guid.NewGuid(), peer, station.Callsign,
            "Simulated incoming HFLink message");
        report?.Invoke(new("Receiving simulated message", Peer: peer));
        if (await onPacket(packet, stop) is not null) report?.Invoke(new("Simulated receipt sent", Peer: peer));
        await onPacket(packet, stop);
    }
}

public sealed class MercuryHfModem(Action<HfModemStatus>? report = null) : IHfModem
{
    public async Task ProbeAsync(HfStation station, CancellationToken stop)
    {
        using var session = await OpenAsync(station, stop);
        await session.CommandAsync("BUFFER", stop, line => Regex.IsMatch(line, @"\ABUFFER [0-9]+\z"));
    }

    public async Task<HfPacket?> SendAsync(HfStation station, HfPacket packet, Action accepted, CancellationToken stop)
    {
        using var session = await OpenAsync(station, stop);
        bool dialled = false;
        try
        {
            dialled = true;
            report?.Invoke(new("Calling station", Peer: packet.To));
            await session.CommandAsync($"CONNECT {station.Callsign} {packet.To}", stop);
            await session.ConnectedAsync(station.Callsign, packet.To, stop);
            report?.Invoke(new("Sending message to Mercury"));
            await session.Data.WriteAsync(packet.Encode(), stop);
            accepted();
            report?.Invoke(new("Sent to Mercury. Waiting for receipt."));
            var reply = HfPacket.Decode(await session.ReadPacketAsync(stop), packet.To, station.Callsign);
            if (reply.Confirms(packet)) report?.Invoke(new("Delivery receipt received"));
            await session.EndAsync("DISCONNECT");
            dialled = false;
            return reply;
        }
        finally
        {
            if (dialled) await session.EndAsync("ABORT");
        }
    }

    public async Task ReceiveAsync(HfStation station, Func<HfPacket, CancellationToken, Task<HfPacket?>> onPacket, CancellationToken stop)
    {
        using var session = await OpenAsync(station, stop);
        bool connected = false;
        try
        {
            await session.CommandAsync("LISTEN ON", stop);
            report?.Invoke(new("Listening for a call"));
            string peer = await session.ConnectedAsync(null, station.Callsign, stop);
            connected = true;
            report?.Invoke(new("Receiving message", Peer: peer));
            var packet = HfPacket.Decode(await session.ReadPacketAsync(stop), peer, station.Callsign);
            var response = await onPacket(packet, stop);
            if (response is not null)
            {
                if (!response.Confirms(packet) || packet.Type != "message") throw new InvalidDataException("Invalid receipt.");
                report?.Invoke(new("Sending delivery receipt"));
                await session.Data.WriteAsync(response.Encode(), stop);
            }
            await session.EndAsync("DISCONNECT");
            connected = false;
        }
        finally
        {
            if (connected) await session.EndAsync("ABORT");
            await session.EndAsync("LISTEN OFF");
        }
    }

    private async Task<Session> OpenAsync(HfStation station, CancellationToken stop)
    {
        // One reconnect before any RF command, for a restarted or momentarily busy TNC.
        for (int attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var control = new TcpClient();
            var data = new TcpClient();
            try
            {
                await control.ConnectAsync(station.Host, station.ControlPort, deadline.Token);
                await data.ConnectAsync(station.Host, station.DataPort, deadline.Token);
                var session = new Session(control, data, report);
                await session.CommandAsync("LISTEN OFF", deadline.Token);
                await session.CommandAsync("PUBLIC OFF", deadline.Token);
                await session.CommandAsync($"MYCALL {station.Callsign}", deadline.Token);
                return session;
            }
            catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
            {
                data.Dispose();
                control.Dispose();
                stop.ThrowIfCancellationRequested();
                if (attempt == 1) throw new IOException("Could not initialise Mercury’s control and data connection.", error);
                await Task.Delay(500, stop);
            }
        }
    }

    private sealed class Session(TcpClient control, TcpClient data, Action<HfModemStatus>? report) : IDisposable
    {
        private readonly NetworkStream _control = control.GetStream();
        public NetworkStream Data { get; } = data.GetStream();

        private async Task<string> ReadControlAsync(CancellationToken stop)
        {
            string line = Encoding.ASCII.GetString(await ReadLineAsync(_control, (byte)'\r', 1024, stop)).Trim();
            if (report is not null && Telemetry(line) is { } status) report(status);
            return line;
        }

        private static HfModemStatus? Telemetry(string line)
        {
            switch (line)
            {
                case "PTT ON": return new(Keyed: true);
                case "PTT OFF": return new(Keyed: false);
                case "BUSY ON": return new(Busy: true);
                case "BUSY OFF": return new(Busy: false);
            }
            if (line.StartsWith("SN ", StringComparison.Ordinal) &&
                double.TryParse(line.AsSpan(3), NumberStyles.Float, CultureInfo.InvariantCulture, out double snr) && double.IsFinite(snr))
                return new(Snr: snr);
            if (line.StartsWith("BUFFER ", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int queued))
                return new(QueuedBytes: queued);
            if (line.StartsWith("BITRATE (", StringComparison.Ordinal) && line.EndsWith(" BPS", StringComparison.Ordinal))
            {
                int end = line.IndexOf(") ", StringComparison.Ordinal);
                if (end >= 10 && end + 2 < line.Length - 4 &&
                    int.TryParse(line.AsSpan(end + 2, line.Length - 4 - end - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int bitrate))
                    return new(Bitrate: bitrate);
            }
            return null;
        }

        public async Task CommandAsync(string command, CancellationToken stop, Func<string, bool>? match = null)
        {
            await _control.WriteAsync(Encoding.ASCII.GetBytes(command + "\r"), stop);
            while (true)
            {
                string line = await ReadControlAsync(stop);
                if (line is "WRONG" or "BUSY" or "NOK") throw new IOException("Mercury rejected " + command.Split(' ')[0] + ".");
                if (match?.Invoke(line) ?? line == "OK") return;
            }
        }

        public async Task<string> ConnectedAsync(string? expectedSource, string expectedDestination, CancellationToken stop)
        {
            while (true)
            {
                string line = await ReadControlAsync(stop);
                if (line == "DISCONNECTED" && expectedSource is not null) throw new IOException("Mercury failed to establish an HF session.");
                if (!line.StartsWith("CONNECTED ", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4 || parts[2] != expectedDestination ||
                    (expectedSource is not null && parts[1] != expectedSource) || parts[3] is not ("500" or "2300" or "2750"))
                    throw new InvalidDataException("Mercury reported an unexpected HF session.");
                string peer = HfStation.Call(expectedSource is null ? parts[1] : parts[2]);
                report?.Invoke(new("Connected", Peer: peer, Bandwidth: int.Parse(parts[3], CultureInfo.InvariantCulture)));
                return HfStation.Call(parts[1]);
            }
        }

        public async Task<byte[]> ReadPacketAsync(CancellationToken stop)
        {
            using var race = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var packet = ReadLineAsync(Data, (byte)'\n', HfPacket.WireLimit, race.Token);
            var disconnect = WaitForDisconnectAsync(race.Token);
            try
            {
                if (await Task.WhenAny(packet, disconnect) == packet || packet.IsCompletedSuccessfully) return await packet;
                await disconnect;
                throw new IOException("Mercury ended the HF session before a complete packet arrived.");
            }
            finally
            {
                await race.CancelAsync();
                try { await Task.WhenAll(packet, disconnect); }
                catch (Exception error) when (error is OperationCanceledException or IOException) { }
            }
        }

        private async Task WaitForDisconnectAsync(CancellationToken stop)
        {
            while (await ReadControlAsync(stop) != "DISCONNECTED") { }
        }

        public async Task EndAsync(string command)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(command == "DISCONNECT" ? 20 : 3));
            try
            {
                await _control.WriteAsync(Encoding.ASCII.GetBytes(command + "\r"), deadline.Token);
                if (command == "DISCONNECT") await WaitForDisconnectAsync(deadline.Token);
                else await ReadControlAsync(deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }

        public void Dispose() { data.Dispose(); control.Dispose(); }
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, byte delimiter, int maxBytes, CancellationToken stop)
    {
        var line = new byte[maxBytes];
        var next = new byte[1];
        for (int i = 0; i < line.Length; i++)
        {
            if (await stream.ReadAsync(next, stop) == 0) throw new IOException("Mercury closed a TCP socket.");
            if (next[0] == delimiter) return line[..i];
            line[i] = next[0];
        }
        throw new InvalidDataException("Mercury input exceeded its frame limit.");
    }
}
