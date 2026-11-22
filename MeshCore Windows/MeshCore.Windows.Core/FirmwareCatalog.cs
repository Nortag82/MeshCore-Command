using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record FirmwareImage(string Target, int MinimumFlashMegabytes, IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Hashes);

/// <summary>Validates packaged firmware before any flashing operation is attempted.</summary>
public static class FirmwareCatalog
{
    private static readonly IReadOnlyDictionary<string, int> Targets = new Dictionary<string, int>(StringComparer.Ordinal) {
        ["v3-headless"] = 8, ["v3-oled"] = 8, ["v4-auto"] = 16, ["v4-tft"] = 16,
        ["tracker-headless"] = 8, ["tracker-screen"] = 8, ["r8-headless"] = 16, ["r8-oled"] = 16, ["tbeam-oled"] = 8
    };
    private static readonly string[] Required = ["bootloader.bin", "partitions.bin", "boot_app0.bin", "firmware.bin"];
    public static IReadOnlyList<string> SupportedTargets => Targets.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static FirmwareImage Verify(string root, string target)
    {
        if (!Targets.TryGetValue(target, out int minimum)) throw new ArgumentException("Choose the exact board and screen.", nameof(target));
        root = Path.GetFullPath(root);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        var entry = manifest.RootElement.GetProperty(target);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in Required)
        {
            string expected = entry.GetProperty(name).GetString() ?? "";
            if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$")) throw new InvalidDataException("Firmware manifest contains an invalid hash.");
            string path = Path.Combine(root, target, name);
            using var input = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Firmware is missing or failed its integrity check.");
            files.Add(name, path);
            hashes.Add(name, expected);
        }
        return new FirmwareImage(target, minimum, files, hashes);
    }

    public static void ValidateWifiCredentials(string ssid, string password)
    {
        ArgumentNullException.ThrowIfNull(ssid); ArgumentNullException.ThrowIfNull(password);
        int ssidBytes = System.Text.Encoding.UTF8.GetByteCount(ssid), passwordBytes = System.Text.Encoding.UTF8.GetByteCount(password);
        if (ssidBytes is < 1 or > 32 || ssid.Contains('\0')) throw new ArgumentException("SSID must be 1–32 UTF-8 bytes.", nameof(ssid));
        if ((passwordBytes != 0 && passwordBytes is < 8 or > 63) || password.Contains('\0')) throw new ArgumentException("Password must be empty or 8–63 UTF-8 bytes.", nameof(password));
    }
}
