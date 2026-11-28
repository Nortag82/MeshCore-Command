using MeshCore.Windows.Core;
using System.Diagnostics;

internal static class Js8PhyChecks
{
    internal static async Task RunAsync()
    {
        var slots = new Js8ReceiveSlots(0);
        var utc = DateTimeOffset.UnixEpoch.AddMinutes(12);
        var complete = new List<Js8ReceiveSlot>();
        slots.Add(new short[12_000], utc.AddSeconds(-1), complete.Add);
        slots.Add(new short[60_000], utc, complete.Add);
        slots.Add(new short[120_000], utc.AddSeconds(5).AddMilliseconds(40), complete.Add);
        if (complete.Single().Start != utc || complete[0].Samples.Length != 180_000)
            throw new InvalidDataException("JS8 slots did not align to UTC across a jittered audio callback.");
        slots.Add(new short[60_000], utc.AddSeconds(15), complete.Add);
        slots.Add(new short[108_000], utc.AddSeconds(21), complete.Add);
        if (complete.Count != 1)
            throw new InvalidDataException("An interrupted JS8 slot must be discarded.");

        string? executable = Environment.GetEnvironmentVariable("MCC_JS8_PHY");
        if (string.IsNullOrWhiteSpace(executable)) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var worker = await Js8PhyClient.OpenAsync(executable, stop.Token);
        var directory = Directory.CreateTempSubdirectory("mcc-js8-phy-check-");
        try
        {
            foreach (int mode in new[] { 0, 4, 1, 2, 8 })
            {
                string wav = Path.Combine(directory.FullName, $"mode-{mode}.wav");
                const string frame = "01ABab23CDcd";
                await worker.EncodeWavAsync(wav, mode, 1500, frame, 3, stop.Token);
                var received = await worker.DecodeWavAsync(wav, mode, 1500, stop.Token);
                if (!received.Any(item => item.Frame == frame && item.Type == 3 && Math.Abs(item.OffsetHz - 1500) < 5))
                    throw new InvalidDataException($"JS8 mode {mode} did not recover its generated frame.");
            }
            if (Environment.GetEnvironmentVariable("MCC_JS8_UPSTREAM_FIXTURE") is { Length: > 0 } fixture)
            {
                var received = await worker.DecodeWavAsync(fixture, 0, 1500, stop.Token);
                if (!received.Any(frame => frame.Heartbeat && frame.Text.Length > 0 && frame.Text != frame.Frame))
                    throw new InvalidDataException("The upstream Normal sample did not produce a readable legacy heartbeat.");
                Console.WriteLine($"Offline upstream sample recovered {received.Length} JS8 frame(s).");
            }
            Console.WriteLine("The native JS8 PHY round-tripped all five modes through PCM WAV without radio hardware.");
            const string expected = "01ABab23CDcd";
            short[] samples;
            using (var reader = new BinaryReader(File.OpenRead(Path.Combine(directory.FullName, "mode-0.wav"))))
            {
                reader.BaseStream.Position = 44;
                samples = new short[(reader.BaseStream.Length - 44) / sizeof(short)];
                for (int index = 0; index < samples.Length; index++) samples[index] = reader.ReadInt16();
            }
            complete.Clear();
            slots.Add(samples.AsSpan(0, 72_000), utc.AddMinutes(1), complete.Add);
            slots.Add(samples.AsSpan(72_000), utc.AddMinutes(1).AddSeconds(6), complete.Add);
            await using var receiving = new Js8ReceiveDecoder(executable, Console.WriteLine);
            var first = await receiving.DecodeAsync(complete.Single(), 0, 1500, stop.Token);
            if (!first.Any(frame => frame.Frame == expected)) throw new InvalidDataException("Live receive path failed to decode its test slot.");
            using var failed = Process.GetProcessById(receiving.WorkerProcessId!.Value);
            failed.Kill();
            await failed.WaitForExitAsync(stop.Token);
            var recovered = await receiving.DecodeAsync(complete.Single(), 0, 1500, stop.Token);
            if (receiving.WorkerProcessId == failed.Id || !recovered.Any(frame => frame.Frame == expected))
                throw new InvalidDataException("The live receive decoder failed to recover after the worker exited.");
            Console.WriteLine("The receive path decoded a UTC-aligned slot and recovered after forced worker exit.");
        }
        finally
        {
            foreach (string wav in Directory.EnumerateFiles(directory.FullName)) File.Delete(wav);
            directory.Delete();
        }
    }
}
