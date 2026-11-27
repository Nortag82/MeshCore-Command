using System.Text;

namespace MeshCore.Windows.Core;

public static class MercuryBroadcast
{
    public const int DefaultPort = 8100;
    public const string Destination = "Broadcast";
    internal const string Protocol = "Mercury Broadcast";
    private const byte End = 0xc0, Escape = 0xdb;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static IReadOnlyList<(int Index, string Name, int FrameBytes)> Modes { get; } = Array.AsReadOnly(
        new (int, string, int)[]
        {
            (1, "DATAC3", 126), (3, "DATAC4", 54), (7, "DATAC15", 30), (12, "MFSK500", 98),
            (11, "MFSK", 98), (0, "DATAC1", 510), (9, "DATAC17", 1184), (10, "QAM16C2", 1183)
        });

    public static int TextLimit(string callsign, int mode)
    {
        int frameSize = Modes.FirstOrDefault(entry => entry.Index == mode).FrameBytes;
        return Math.Clamp(frameSize - 6 - callsign.Length, 0, HfPacket.MercuryChatTextLimit);
    }

    internal static void ValidateText(string callsign, string text, int mode)
    {
        int limit = TextLimit(callsign, mode);
        if (string.IsNullOrWhiteSpace(text) || Utf8.GetByteCount(text) > limit ||
            text.Any(character => char.IsControl(character) && character != '\t'))
            throw new ArgumentException($"Enter one line of 1–{limit} UTF-8 bytes for this broadcast mode.");
    }

    internal static byte[] Encode(string from, string text)
    {
        var frame = new List<byte> { End, 0x00 };
        foreach (byte value in Utf8.GetBytes($"{from}: {text}\n"))
        {
            if (value is End or Escape)
            {
                frame.Add(Escape);
                frame.Add(value == End ? (byte)0xdc : (byte)0xdd);
            }
            else frame.Add(value);
        }
        frame.Add(End);
        return frame.ToArray();
    }

    internal static async Task ReceiveAsync(Stream stream, Func<HfPacket, CancellationToken, Task<HfPacket?>> onPacket, CancellationToken stop)
    {
        byte[] incoming = new byte[4096], frame = new byte[1185];
        int length = 0;
        bool inFrame = false, escaped = false;
        int count;
        while ((count = await stream.ReadAsync(incoming, stop)) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte value = incoming[i];
                if (value == End)
                {
                    if (inFrame && !escaped && Decode(frame.AsSpan(0, length)) is { } packet)
                        await onPacket(packet, stop);
                    inFrame = true;
                    escaped = false;
                    length = 0;
                }
                else if (inFrame)
                {
                    if (escaped)
                    {
                        if (value is not (0xdc or 0xdd)) { inFrame = false; continue; }
                        value = value == 0xdc ? End : Escape;
                        escaped = false;
                    }
                    else if (value == Escape) { escaped = true; continue; }
                    if (length == frame.Length) { inFrame = false; continue; }
                    frame[length++] = value;
                }
            }
        }
        throw new IOException("Mercury closed the broadcast socket.");
    }

    private static HfPacket? Decode(ReadOnlySpan<byte> frame)
    {
        // The standalone client uses 0x00 for text. Binary modem frames and AX.25 traffic use the same port.
        if (frame.Length < 2 || frame[0] != 0x00) return null;
        try
        {
            string line = Utf8.GetString(frame[1..]).TrimEnd('\r', '\n');
            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0) return null;
            string from = HfStation.Call(line[..separator]);
            string text = line[(separator + 2)..];
            if (string.IsNullOrWhiteSpace(text) || Utf8.GetByteCount(text) > HfPacket.MercuryChatTextLimit ||
                text.Any(character => char.IsControl(character) && character != '\t')) return null;
            return new(Protocol, "message", Guid.NewGuid(), Guid.NewGuid(), from, Destination, text);
        }
        catch (ArgumentException) { return null; }
    }
}
