using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MeshCore.Windows.Core;

/// <summary>Runs a pinned espflash executable without a shell or visible console.</summary>
public sealed class EspFlashTool : IFlashTool
{
    // SHA-256 of espflash.exe from the verified upstream v4.7.0 Windows x64 release archive.
    private const string ExpectedHash = "9fc8f3ace81e3180d2ba0b72c31a81e3f47ac580e3c9667954b58016d7a3fd87";
    private readonly string _path;
    public EspFlashTool(string path) { _path = Path.GetFullPath(path); }

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using (var file = File.OpenRead(_path))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The firmware flashing tool failed its integrity check.");
        var start = new ProcessStartInfo(_path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(_path)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("ESPFLASH_PORT"); start.Environment.Remove("ESPFLASH_BAUD");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the firmware flashing tool.");
        Task<string> output = ReadTailAsync(process.StandardOutput), error = ReadTailAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Firmware tool timed out; check the USB connection.");
        }
        string stdout = await output, stderr = await error;
        if (process.ExitCode != 0) throw new IOException("Firmware tool failed: " + stderr);
        return stdout + "\n" + stderr;
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder(); char[] buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 12000) tail.Remove(0, tail.Length - 12000);
        }
        return tail.ToString();
    }
}
