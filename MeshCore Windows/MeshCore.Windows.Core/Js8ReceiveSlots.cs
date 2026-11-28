namespace MeshCore.Windows.Core;

public sealed record Js8ReceiveSlot(DateTimeOffset Start, short[] Samples);

public sealed class Js8ReceiveSlots
{
    public const int SampleRate = 12_000;
    private readonly int _length;
    private long? _next;
    private short[]? _slot;

    public Js8ReceiveSlots(int mode)
    {
        PeriodSeconds = mode switch { 0 => 15, 1 => 10, 2 => 6, 4 => 30, 8 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        _length = PeriodSeconds * SampleRate;
    }

    public int PeriodSeconds { get; }

    public void Add(ReadOnlySpan<short> samples, DateTimeOffset observedStart, Action<Js8ReceiveSlot> complete)
    {
        if (samples.IsEmpty) return;
        long observed = observedStart.ToUnixTimeMilliseconds() * (SampleRate / 1000);
        if (_next is null || Math.Abs(_next.Value - observed) > SampleRate / 5)
        {
            _slot = null;
            _next = observed;
        }

        long next = _next.Value;
        while (!samples.IsEmpty)
        {
            int offset = (int)((next % _length + _length) % _length);
            if (offset == 0) _slot = new short[_length];
            int take = Math.Min(samples.Length, _length - offset);
            if (_slot is not null) samples[..take].CopyTo(_slot.AsSpan(offset, take));
            next += take;
            samples = samples[take..];
            if (offset + take == _length && _slot is { } full)
            {
                _slot = null;
                complete(new(DateTimeOffset.FromUnixTimeSeconds((next - _length) / SampleRate), full));
            }
        }
        _next = next;
    }
}
