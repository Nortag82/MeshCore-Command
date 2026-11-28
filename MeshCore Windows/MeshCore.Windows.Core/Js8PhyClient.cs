using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MeshCore.Windows.Core;

public sealed record Js8PhyFrame(string Frame, int Type, double OffsetHz, int Snr, double TimeOffsetSeconds,
    string Text = "", string From = "", string To = "", bool Heartbeat = false, bool LowConfidence = false);

public sealed class Js8PhyClient : IAsyncDisposable
{
    public const string Upstream = "4c592bd9a034f18178a3e7179db92acb14939668";
    private const int Protocol = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Process _process;
    private long _nextId;

    private Js8PhyClient(Process process) => _process = process;
    internal int ProcessId => _process.Id;

    public static async Task<Js8PhyClient> OpenAsync(string executable, CancellationToken stop = default)
    {
        string path = Path.GetFullPath(executable);
        var start = new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        var process = Process.Start(start) ?? throw new IOException("Could not launch the headless JS8 worker.");
        process.BeginErrorReadLine();
        var client = new Js8PhyClient(process);
        try
        {
            var status = await client.RequestAsync("status", null, 0, 0, null, 0, stop);
            if (status.GetProperty("upstream").GetString() != Upstream || !status.GetProperty("fileOnly").GetBoolean() ||
                status.GetProperty("sampleRate").GetInt32() != 12_000)
                throw new InvalidDataException("Unexpected JS8 worker version or capabilities.");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<Js8PhyFrame[]> DecodeWavAsync(string path, int mode, int offsetHz, CancellationToken stop = default)
    {
        var reply = await RequestAsync("decodeWav", Path.GetFullPath(path), mode, offsetHz, null, 0, stop);
        return reply.GetProperty("frames").Deserialize<Js8PhyFrame[]>(Json)
            ?? throw new InvalidDataException("The JS8 worker returned an invalid frame list.");
    }

    public async Task EncodeWavAsync(string path, int mode, int offsetHz, string frame, int type, CancellationToken stop = default) =>
        await RequestAsync("encodeWav", Path.GetFullPath(path), mode, offsetHz, frame, type, stop);

    private async Task<JsonElement> RequestAsync(string operation, string? path, int mode, int offsetHz,
        string? frame, int type, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string id = (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string request = JsonSerializer.Serialize(new { protocol = Protocol, id, operation, path, mode, offsetHz, frame, type });
        try
        {
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token);
            await _process.StandardInput.FlushAsync(deadline.Token);
            var line = await _process.StandardOutput.ReadLineAsync(deadline.Token);
            if (line is null || line.Length > 256_000) throw new IOException("No valid response from the JS8 worker.");
            using var response = JsonDocument.Parse(line);
            var message = response.RootElement;
            if (message.GetProperty("protocol").GetInt32() != Protocol || message.GetProperty("id").GetString() != id)
                throw new InvalidDataException("The JS8 worker replied to a different request.");
            if (message.TryGetProperty("error", out var error))
                throw new InvalidDataException(error.GetString() ?? "JS8 worker error.");
            return message.GetProperty("result").Clone();
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited) _process.Kill();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (_process)
        {
            try { _process.StandardInput.Close(); }
            catch (IOException) { }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited) _process.Kill();
                await _process.WaitForExitAsync();
            }
        }
    }
}
