namespace MeshCore.Windows.Core;

/// <summary>Bounded, acknowledged queue matching the existing desktop bridge.</summary>
public sealed class FrameQueue
{
    public sealed record Item(uint Sequence, byte[] Payload);
    private readonly Queue<Item> _frames = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private uint _sequence;
    private FrameQueue? _forward;

    public FrameQueue(int capacity = 512)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Enqueue(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > CompanionFrames.MaximumPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload));
        lock (_gate)
        {
            if (_forward is not null) { _forward.Enqueue(payload); return; }
            if (_frames.Count == _capacity) throw new InvalidOperationException("Frame queue overflow.");
            _sequence = _sequence == uint.MaxValue ? 1 : _sequence + 1;
            _frames.Enqueue(new Item(_sequence, payload.ToArray()));
        }
    }

    // Used only while attaching a freshly identified transport. Holding the source lock
    // preserves frame order when the reader is concurrently receiving data.
    internal void ForwardTo(FrameQueue destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, this)) throw new ArgumentException("A queue cannot forward to itself.");
        lock (_gate)
        {
            if (_forward is not null) throw new InvalidOperationException("Queue is already forwarding.");
            lock (destination._gate)
            {
                if (destination._forward is not null || _frames.Count > destination._capacity - destination._frames.Count)
                    throw new InvalidOperationException("Frame queue cannot accept the reconnect backlog.");
                while (_frames.TryDequeue(out var item))
                {
                    destination._sequence = destination._sequence == uint.MaxValue ? 1 : destination._sequence + 1;
                    destination._frames.Enqueue(new Item(destination._sequence, item.Payload));
                }
                _forward = destination;
            }
        }
    }

    public Item? PeekAfterAcknowledgement(uint? acknowledgement)
    {
        lock (_gate)
        {
            if (_frames.TryPeek(out var first) && acknowledgement == first.Sequence) _frames.Dequeue();
            if (!_frames.TryPeek(out var next)) return null;
            return new Item(next.Sequence, (byte[])next.Payload.Clone());
        }
    }

    internal byte[]? TakeFirstResponse(byte responseCode, int minimumLength)
    {
        lock (_gate)
        {
            Item? found = null;
            int count = _frames.Count;
            for (int i = 0; i < count; i++)
            {
                var item = _frames.Dequeue();
                if (found is null && item.Payload.Length >= minimumLength && item.Payload[0] == responseCode) found = item;
                else _frames.Enqueue(item);
            }
            return found is null ? null : (byte[])found.Payload.Clone();
        }
    }
}
