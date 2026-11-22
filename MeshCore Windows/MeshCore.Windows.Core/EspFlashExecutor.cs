using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public interface IFlashTool
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record FlashProgress(string Phase, int Percent, string Message);

/// <summary>Flash orchestration for espflash 4.7.0. Not enabled in the preview UI.</summary>
public sealed class EspFlashExecutor
{
    private readonly IFlashTool _tool;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public EspFlashExecutor(IFlashTool tool) { _tool = tool ?? throw new ArgumentNullException(nameof(tool)); }

    public async Task FlashAsync(string firmwareRoot, string target, string port, IReadOnlyCollection<string> availablePorts,
        IProgress<FlashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!availablePorts.Contains(port, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Select an available COM port.", nameof(port));
        FirmwareImage image = FirmwareCatalog.Verify(firmwareRoot, target);
        await _gate.WaitAsync(cancellationToken);
        string folder = Path.Combine(Path.GetTempPath(), "mcc-flash-" + Guid.NewGuid().ToString("N"));
        var copies = new List<string>(); bool writingStarted = false;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var file in image.Files)
            {
                string copy = Path.Combine(folder, file.Key); File.Copy(file.Value, copy);
                copies.Add(copy);
                await using var data = File.OpenRead(copy);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(data, cancellationToken)).Equals(image.Hashes[file.Key], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Firmware changed while preparing to flash.");
            }
            progress?.Report(new FlashProgress("connecting", 0, "Checking board and flash capacity…"));
            string[] common = ["--port", port, "--chip", "esp32s3", "--non-interactive", "--skip-update-check", "--after", "no-reset"];
            string info = await _tool.RunAsync(new[] { "board-info" }.Concat(common).ToArray(), TimeSpan.FromSeconds(60), cancellationToken);
            int capacity = NativeFlashPlan.ParseFlashSize(info);
            var plan = NativeFlashPlan.Create(firmwareRoot, target, port, capacity);
            for (int i = 0; i < plan.Count; i++)
            {
                var item = plan[i]; string file = Path.Combine(folder, Path.GetFileName(item.FilePath));
                progress?.Report(new FlashProgress("flashing", i * 20, $"Writing firmware part {i + 1} of {plan.Count}…"));
                writingStarted = true;
                await _tool.RunAsync(new[] { "write-bin" }.Concat(common).Concat(new[] { "--baud", "921600", item.Offset, file }).ToArray(), TimeSpan.FromMinutes(4), cancellationToken);
            }
            for (int i = 0; i < plan.Count; i++)
            {
                var item = plan[i]; string file = Path.Combine(folder, Path.GetFileName(item.FilePath));
                progress?.Report(new FlashProgress("verifying", 80 + i * 5, $"Verifying firmware part {i + 1} of {plan.Count}…"));
                await using var input = File.OpenRead(file);
                string expected = Convert.ToHexString(await MD5.HashDataAsync(input, cancellationToken));
                string output = await _tool.RunAsync(new[] { "checksum-md5" }.Concat(common).Concat(new[] { item.Offset, input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray(), TimeSpan.FromMinutes(2), cancellationToken);
                var match = Regex.Match(output, @"(?im)^\s*0x([0-9a-f]{32})\s*$");
                if (!match.Success || !match.Groups[1].Value.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Firmware verification failed.");
            }
            await _tool.RunAsync(new[] { "reset", "--port", port, "--chip", "esp32s3", "--non-interactive", "--skip-update-check" }, TimeSpan.FromSeconds(30), cancellationToken);
            progress?.Report(new FlashProgress("complete", 100, "Firmware verified; ready for Wi-Fi provisioning."));
        }
        catch (OperationCanceledException error) when (writingStarted)
        { throw new OperationCanceledException("Flashing was cancelled after writing began; the firmware may be incomplete.", error, cancellationToken); }
        catch (Exception error) when (writingStarted)
        { throw new IOException("Flashing stopped after writing began; the firmware may be incomplete. " + error.Message, error); }
        finally
        {
            foreach (string file in copies) try { File.Delete(file); } catch (IOException) { }
            try { if (Directory.Exists(folder)) Directory.Delete(folder); } catch (IOException) { }
            _gate.Release();
        }
    }
}
