using System.Buffers.Binary;

namespace MeshCore.Windows.Core;

/// <summary>MeshCore companion stream framing, independent of the transport.</summary>
public static class CompanionFrames
{
    public const int MaximumPayloadLength = 176;

    public static async ValueTask<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[3];
        // Match the current bridge: discard noise until the radio's frame marker.
        do
        {
            await stream.ReadExactlyAsync(header.AsMemory(0, 1), cancellationToken);
        } while (header[0] != (byte)'>');

        await stream.ReadExactlyAsync(header.AsMemory(1, 2), cancellationToken);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        if (length is < 1 or > MaximumPayloadLength)
            throw new InvalidDataException("Invalid companion frame length.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    public static byte[] EncodeOutgoing(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[payload.Length + 3];
        frame[0] = (byte)'<';
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }
}
