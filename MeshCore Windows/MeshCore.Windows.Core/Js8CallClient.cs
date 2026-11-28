using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record Js8LinkSettings(int Port = 2442, string PublicGroup = "@ALLCALL", string Relay = "")
{
    public Js8LinkSettings Validate()
    {
        string group = PublicGroup?.Trim().ToUpperInvariant() ?? "";
        if (Port is < 1 or > 65535 || !Regex.IsMatch(group, @"\A@[A-Z0-9/]{1,8}\z") ||
            group is "@HB" or "@APRSIS")
            throw new ArgumentException("Enter the JS8Call TCP port and an open JS8Call group such as @ALLCALL.");
        return this with { PublicGroup = group, Relay = string.IsNullOrWhiteSpace(Relay) ? "" : HfStation.Call(Relay) };
    }
}

public readonly record struct Js8ApiMessage(string Type, string Value, JsonElement Params);
public sealed record Js8Received(string From, string To, string Text, string Command, DateTimeOffset Time, int? Snr);

public sealed class Js8CallClient : IDisposable
{
    private readonly TcpClient _socket = new();
    private readonly SemaphoreSlim _write = new(1);
    private long _requestId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task ConnectAsync(int port, CancellationToken stop)
    {
        await _socket.ConnectAsync(IPAddress.Loopback, port, stop);
        var stream = _socket.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
    }

    public async Task RequestAsync(string type, string value, CancellationToken stop)
    {
        var writer = _writer ?? throw new InvalidOperationException("Connect to JS8Call first.");
        long id = Interlocked.Increment(ref _requestId);
        string line = JsonSerializer.Serialize(new { type, value, @params = new { _ID = id } });
        await _write.WaitAsync(stop);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), stop);
            await writer.FlushAsync(stop);
        }
        finally { _write.Release(); }
    }

    public async Task<Js8ApiMessage> ReadAsync(CancellationToken stop)
    {
        var reader = _reader ?? throw new InvalidOperationException("Connect to JS8Call first.");
        while (await reader.ReadLineAsync(stop) is { } line)
        {
            if (line.Length == 0) continue;
            if (line.Length > 4_000_000) throw new InvalidDataException("JS8Call returned an oversized API response.");
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Text(root, "type") is { Length: > 0 } type)
                return new(type, Text(root, "value"), root.TryGetProperty("params", out var parameters) ? parameters.Clone() : default);
        }
        throw new IOException("JS8Call closed its API connection.");
    }

    public static string Compose(string destination, string text, bool publicMessage, string? relay = null)
    {
        destination = publicMessage ? new Js8LinkSettings(PublicGroup: destination).Validate().PublicGroup : HfStation.Call(destination);
        string body = text.Trim().ToUpperInvariant();
        if (body.Length is < 1 or > 180 || !Regex.IsMatch(body, @"\A[A-Z0-9 .,?!/@+()\-']+\z"))
            throw new ArgumentException("Enter up to 180 characters using letters, numbers, spaces and common JS8 punctuation.");
        if (relay is not null) return $"{HfStation.Call(relay)} MSG TO:{destination} {body}";
        // An extra space makes JS8Call encode the body as plain text, even if it starts with a command verb.
        return publicMessage ? $"{destination}  {body}" : $"{destination} MSG {body}";
    }

    public static string RelayQuery(string relay, int? id = null) => id is null
        ? $"{HfStation.Call(relay)} QUERY MSGS"
        : id > 0 ? $"{HfStation.Call(relay)} QUERY MSG {id.Value.ToString(CultureInfo.InvariantCulture)}"
        : throw new ArgumentOutOfRangeException(nameof(id));

    public static Js8Received? DecodeReceived(JsonElement fields, string? liveValue = null)
    {
        if (!TryCall(Text(fields, "FROM"), out string call)) return null;
        string to = Text(fields, "TO").Trim().ToUpperInvariant();
        string command = Text(fields, "CMD").Trim().ToUpperInvariant();
        string text = Text(fields, "TEXT");
        if (liveValue is not null)
        {
            if (text.Length == 0) text = liveValue;
            string sender = $"{call}: ";
            if (text.StartsWith(sender, StringComparison.OrdinalIgnoreCase)) text = text[sender.Length..];
            text = text.Trim().TrimEnd('♢', '♦', '…').TrimEnd();
            if (to.Length > 0 && (text.Equals(to, StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(to + " ", StringComparison.OrdinalIgnoreCase)))
            {
                text = text[to.Length..].TrimStart();
                if (text.Equals(command, StringComparison.OrdinalIgnoreCase)) text = "";
                else if (command.Length > 0 && text.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase))
                    text = text[(command.Length + 1)..];
            }
        }
        return new(call, to, text.Trim(), command, Time(fields), Number(fields, "SNR"));
    }

    public static string Text(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    public static int? Number(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;

    public static DateTimeOffset Time(JsonElement fields)
    {
        if (fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("UTC", out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long milliseconds) && milliseconds is > 0 and < 253402300799999)
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return DateTimeOffset.TryParse(Text(fields, "UTC"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : DateTimeOffset.UtcNow;
    }

    public static bool TryCall(string value, out string callsign)
    {
        try { callsign = HfStation.Call(value); return true; }
        catch (ArgumentException) { callsign = ""; return false; }
    }

    public void Dispose()
    {
        _socket.Dispose();
        _reader?.Dispose();
        // All API requests explicitly flush their output. Closing the socket discards any partial request.
    }
}
