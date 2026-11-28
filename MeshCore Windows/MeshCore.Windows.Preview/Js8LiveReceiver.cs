using System.Diagnostics;
using System.Threading.Channels;
using MeshCore.Windows.Core;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace MeshCore.Windows.Preview;

internal sealed record Js8LiveResult(DateTimeOffset Start, Js8PhyFrame[] Frames);

internal static class Js8LiveReceiver
{
    internal static async Task RunAsync(string executable, string endpoint, int mode, int offset,
        IProgress<string> status, IProgress<double> level, IProgress<Js8LiveResult> decoded, CancellationToken stop)
    {
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDevice(endpoint);
        if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
            throw new InvalidOperationException("The selected radio input is unavailable.");
        using var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
        var format = capture.WaveFormat;
        if (format is WaveFormatExtensible extensible) format = extensible.ToStandardWaveFormat();
        if (format.Channels < 1 || format.SampleRate < 8000 ||
            format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) ||
            format.BitsPerSample is not (8 or 16 or 24 or 32) ||
            format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample != 32)
            throw new InvalidOperationException("The selected input uses an unsupported audio format.");
        var resampler = new WdlResampler();
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(format.SampleRate, Js8ReceiveSlots.SampleRate);
        var slots = new Js8ReceiveSlots(mode);
        var pending = Channel.CreateBounded<Js8ReceiveSlot>(new BoundedChannelOptions(2)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        float[] output = [];
        short[] pcm = [];
        long lastLevel = Stopwatch.GetTimestamp();
        capture.DataAvailable += (_, audio) =>
        {
            if (stop.IsCancellationRequested) return;
            var observed = DateTimeOffset.UtcNow;
            int frames = audio.BytesRecorded / format.BlockAlign;
            if (frames == 0) return;
            var start = observed.AddSeconds(-frames / (double)format.SampleRate - resampler.GetCurrentLatency());
            int inputCount = resampler.ResamplePrepare(frames, 1, out var input, out int inputOffset);
            float peak = 0;
            for (int index = 0; index < inputCount; index++)
            {
                float sample = HfWaterfallSpectrum.ReadMonoFrame(audio.Buffer.AsSpan(index * format.BlockAlign), format);
                input[inputOffset + index] = sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            if (Stopwatch.GetElapsedTime(lastLevel) >= TimeSpan.FromMilliseconds(200))
            {
                level.Report(20 * Math.Log10(Math.Max(peak, 0.00001f)));
                lastLevel = Stopwatch.GetTimestamp();
            }
            int capacity = (int)Math.Ceiling(frames * (double)Js8ReceiveSlots.SampleRate / format.SampleRate) + 128;
            if (output.Length < capacity) { output = new float[capacity]; pcm = new short[capacity]; }
            int count = resampler.ResampleOut(output, 0, inputCount, capacity, 1);
            for (int index = 0; index < count; index++)
                pcm[index] = (short)Math.Round(Math.Clamp(output[index], -1, 1) * short.MaxValue);
            slots.Add(pcm.AsSpan(0, count), start, slot => pending.Writer.TryWrite(slot));
        };
        capture.RecordingStopped += (_, stopped) => pending.Writer.TryComplete(stopped.Exception ??
            (stop.IsCancellationRequested ? null : new IOException("The radio audio input stopped.")));
        await using var decoder = new Js8ReceiveDecoder(executable, status.Report);
        capture.StartRecording();
        status.Report($"Receiving from {device.FriendlyName} · waiting for a complete {slots.PeriodSeconds} s UTC slot");
        try
        {
            await foreach (var slot in pending.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                var frames = await decoder.DecodeAsync(slot, mode, offset, stop).ConfigureAwait(false);
                decoded.Report(new(slot.Start, frames));
            }
        }
        finally { capture.StopRecording(); }
    }
}
