using Microsoft.Win32;
using System.Security.Cryptography;

namespace MeshCore.Windows.Preview;

internal static class Js8CallPrerequisite
{
    internal const string Version = "3.0.3";
    internal const string FileName = "JS8Call-" + Version + "-installer.exe";
    private const string DownloadUrl = "https://github.com/JS8Call-improved/JS8Call-improved/releases/download/v" + Version + "/" + FileName;
    private const int InstallerBytes = 31_180_832;
    private const string InstallerSha256 = "AFFDFABB91D2AA59CB81D6859679CB19E09C18AC00EA1CE9AE0E2730A5A00285";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static bool IsInstalled() => FindExecutable() is not null;

    internal static string? FindExecutable()
    {
        foreach (var parent in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        })
            if (ExecutableIn(Path.Combine(parent, "JS8Call")) is { } executable) return executable;

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var installed = root.OpenSubKey(UninstallKey);
                if (installed is null) continue;
                foreach (string name in installed.GetSubKeyNames())
                {
                    using var entry = installed.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is string title &&
                        (title.Equals("JS8Call", StringComparison.OrdinalIgnoreCase) || title.StartsWith("JS8Call ", StringComparison.OrdinalIgnoreCase)) &&
                        entry.GetValue("InstallLocation") is string directory && ExecutableIn(directory) is { } executable)
                        return executable;
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Try the other registry views when a key is unavailable.
            }
        }
        return null;
    }

    private static string? ExecutableIn(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) return null;
        string path = Path.Combine(directory, "JS8Call.exe");
        return File.Exists(path) ? path : null;
    }

    internal static async Task DownloadAsync(string path, CancellationToken stop)
    {
        using var client = new HttpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != InstallerBytes)
            throw new InvalidDataException("The JS8Call download size does not match the official installer.");

        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        int bytes = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, deadline.Token)) != 0)
        {
            bytes += read;
            if (bytes > InstallerBytes) throw new InvalidDataException("The JS8Call download exceeds the expected size.");
            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
        }
        if (bytes != InstallerBytes || Convert.ToHexString(hash.GetHashAndReset()) != InstallerSha256)
            throw new InvalidDataException("The JS8Call download did not match the official installer checksum.");
    }
}
