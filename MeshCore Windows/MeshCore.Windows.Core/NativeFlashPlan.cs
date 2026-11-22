using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record FlashOperation(string Offset, string FilePath);

/// <summary>Builds a verified ESP32-S3 flashing plan without opening a device.</summary>
public static class NativeFlashPlan
{
    private static readonly (string Name, string Offset)[] Layout = [
        ("bootloader.bin", "0x0"), ("partitions.bin", "0x8000"), ("boot_app0.bin", "0xe000"), ("firmware.bin", "0x10000")
    ];

    public static IReadOnlyList<FlashOperation> Create(string firmwareRoot, string target, string port, int detectedFlashMegabytes)
    {
        if (port is null || !Regex.IsMatch(port, "^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new ArgumentException("Select a valid COM port.", nameof(port));
        FirmwareImage image = FirmwareCatalog.Verify(firmwareRoot, target);
        if (detectedFlashMegabytes < image.MinimumFlashMegabytes) throw new InvalidDataException("The selected firmware requires a larger flash chip.");
        return Layout.Select(item => new FlashOperation(item.Offset, image.Files[item.Name])).ToArray();
    }

    public static int ParseFlashSize(string boardInfo)
    {
        if (boardInfo is null || !Regex.IsMatch(boardInfo, @"Chip type:\s*esp32s3\b", RegexOptions.IgnoreCase)) throw new InvalidDataException("The device is not a confirmed ESP32-S3.");
        if (Regex.IsMatch(boardInfo, @"could not detect flash size|defaulting to", RegexOptions.IgnoreCase)) throw new InvalidDataException("The flash size could not be confirmed.");
        var match = Regex.Match(boardInfo, @"Flash size:\s*([0-9]+)\s*MB\b", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int size) || size <= 0) throw new InvalidDataException("The flash size could not be confirmed.");
        return size;
    }
}
