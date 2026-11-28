using System.ComponentModel;
using System.Text.Json;

namespace MeshCore.Windows.Core;

public sealed class Js8ReceiveDecoder(string executable, Action<string> report) : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("mcc-js8-rx-");
    private Js8PhyClient? _worker;
    public int? WorkerProcessId => _worker?.ProcessId;

    public async Task<Js8PhyFrame[]> DecodeAsync(Js8ReceiveSlot slot, int mode, int offset, CancellationToken stop)
    {
        string path = Path.Combine(_directory.FullName, "slot.wav");
        try
        {
            WriteWav(path, slot.Samples);
            for (int attempt = 0; ; attempt++)
            {
                stop.ThrowIfCancellationRequested();
                try
                {
                    if (_worker is null)
                    {
                        _worker = await Js8PhyClient.OpenAsync(executable, stop).ConfigureAwait(false);
                        report($"JS8 PHY worker running · PID {_worker.ProcessId}");
                    }
                    return await _worker.DecodeWavAsync(path, mode, offset, stop).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or Win32Exception or
                    KeyNotFoundException or InvalidOperationException or OperationCanceledException)
                {
                    await ReleaseWorkerAsync().ConfigureAwait(false);
                    stop.ThrowIfCancellationRequested();
                    if (attempt != 0) throw;
                    report($"Restarting JS8 PHY worker · {error.Message}");
                    await Task.Delay(500, stop).ConfigureAwait(false);
                }
            }
        }
        finally { File.Delete(path); }
    }

    private static void WriteWav(string path, short[] samples)
    {
        int bytes = checked(samples.Length * sizeof(short));
        const int rate = Js8ReceiveSlots.SampleRate;
        using var stream = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        stream.Write("RIFF"u8); stream.Write(bytes + 36); stream.Write("WAVEfmt "u8); stream.Write(16);
        stream.Write((short)1); stream.Write((short)1); stream.Write(rate); stream.Write(rate * 2);
        stream.Write((short)2); stream.Write((short)16); stream.Write("data"u8); stream.Write(bytes);
        foreach (short sample in samples) stream.Write(sample);
    }

    private async Task ReleaseWorkerAsync()
    {
        if (_worker is not { } worker) return;
        _worker = null;
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await ReleaseWorkerAsync().ConfigureAwait(false); }
        finally { _directory.Delete(); }
    }
}
