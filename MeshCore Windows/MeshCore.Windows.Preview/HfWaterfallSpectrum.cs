using System.Buffers.Binary;
using NAudio.Dsp;
using NAudio.Wave;

namespace MeshCore.Windows.Preview;

internal sealed class HfWaterfallSpectrum(int sampleRate)
{
    public const int FramesPerSecond = 20;
    private const int Order = 12;
    private const int Size = 1 << Order;
    private static readonly float[] Window = Enumerable.Range(0, Size)
        .Select(index => (float)FastFourierTransform.HannWindow(index, Size)).ToArray();
    private static readonly float AmplitudeCorrection = 2 * Size / Window.Sum();
    private readonly float[] _samples = new float[Size];
    private readonly Complex[] _bins = new Complex[Size];
    private int _next, _count, _sinceFrame;
    private float[]? _latest;

    public int MaximumHz { get; } = Math.Min(4000, sampleRate / 2);
    public double BinHz => (double)sampleRate / Size;
    public float[]? TakeFrame() => Interlocked.Exchange(ref _latest, null);

    public void AddAudio(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        int bytesPerChannel = format.BitsPerSample / 8;
        bool floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat;
        for (int offset = 0; offset + format.BlockAlign <= buffer.Length; offset += format.BlockAlign)
        {
            float sum = 0;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                var bytes = buffer[(offset + channel * bytesPerChannel)..];
                float sample = format.BitsPerSample switch
                {
                    16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                    24 => ((bytes[0] << 8) | (bytes[1] << 16) | (bytes[2] << 24)) / 2147483648f,
                    32 when floatingPoint => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                    32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
                    _ => (bytes[0] - 128) / 128f
                };
                if (float.IsFinite(sample)) sum += Math.Clamp(sample, -1, 1);
            }
            Add(sum / format.Channels);
        }
    }

    public void Add(float sample)
    {
        _samples[_next] = sample;
        _next = (_next + 1) % Size;
        _count = Math.Min(_count + 1, Size);
        if (++_sinceFrame < sampleRate / FramesPerSecond || _count < Size) return;
        _sinceFrame = 0;
        for (int index = 0; index < Size; index++)
        {
            _bins[index].X = _samples[(_next + index) % Size] * Window[index];
            _bins[index].Y = 0;
        }
        FastFourierTransform.FFT(true, Order, _bins);
        var spectrum = new float[(int)Math.Ceiling(MaximumHz / BinHz) + 1];
        for (int bin = 0; bin < spectrum.Length; bin++)
        {
            float real = _bins[bin].X, imaginary = _bins[bin].Y;
            float amplitude = MathF.Sqrt(real * real + imaginary * imaginary) * AmplitudeCorrection;
            spectrum[bin] = 20 * MathF.Log10(Math.Max(amplitude, 0.000001f));
        }
        Interlocked.Exchange(ref _latest, spectrum);
    }
}
