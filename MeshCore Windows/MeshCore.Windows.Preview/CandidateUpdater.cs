using System.Diagnostics;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

/// <summary>Update checks for the explicitly installed native candidate only.</summary>
internal sealed class CandidateUpdater : IDisposable
{
    private readonly HttpClient _http = UpdaterClient.CreateClient();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string CurrentVersion { get; }
    private CandidateUpdater(string version) => CurrentVersion = version;

    public static CandidateUpdater? ForCurrentInstallation()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
            string executable = VersionedInstall.ResolveCurrent(root);
            if (!string.Equals(Path.GetFullPath(executable), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)) return null;
            string version = Path.GetFileName(Path.GetDirectoryName(executable))!;
            return new CandidateUpdater(version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    public async Task<InstallerRelease?> CheckAsync()
    {
        await _gate.WaitAsync(_stop.Token);
        try { return await new UpdaterClient(_http).CheckAsync(CurrentVersion, _stop.Token); }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(string version)
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            using var process = Process.GetCurrentProcess();
            var start = await UpdateHandoff.PrepareAsync(new UpdaterClient(_http), CurrentVersion, version, Path.GetTempPath(), process.Id,
                process.StartTime.ToUniversalTime().Ticks, _stop.Token);
            using var installer = Process.Start(start) ?? throw new IOException("Windows did not start the verified installer.");
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { _stop.Cancel(); _http.Dispose(); _stop.Dispose(); }
}
