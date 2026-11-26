using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace MeshCore.Windows.Installer;

internal static class WebView2Prerequisite
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string BootstrapperSha256 = "FC2972DA71D43E0C57B8D85A5CB9B200ED221F723CF43052D08CA722EB0BB22E";
    internal const string DownloadPage = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download";

    internal static Version? FindInstalledVersion() => FindInstalledVersion(ReadRegistry);

    internal static Version? FindInstalledVersion(Func<RegistryHive, RegistryView, string?> readVersion)
    {
        return Parse(readVersion(RegistryHive.LocalMachine, RegistryView.Registry32)) ??
            Parse(readVersion(RegistryHive.CurrentUser, RegistryView.Default));

        static Version? Parse(string? version) =>
            Version.TryParse(version, out var parsed) && parsed.Revision >= 0 && parsed > new Version(0, 0, 0, 0) ? parsed : null;
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(RegistryPath);
            return key?.GetValue("pv") as string;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    internal static void RequireInstalled()
    {
        if (FindInstalledVersion() is null)
            throw new InvalidOperationException("The Microsoft Edge WebView2 Runtime is required. Open MeshCore Command Setup and choose Install WebView2.");
    }

    internal static async Task InstallAsync()
    {
        if (FindInstalledVersion() is not null) return;

        var directory = Directory.CreateTempSubdirectory("mcc-webview2-");
        string file = Path.Combine(directory.FullName, "MicrosoftEdgeWebview2Setup.exe");
        Process? process = null;
        try
        {
            using (var embedded = OpenBootstrapper())
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await embedded.CopyToAsync(output);

            using (var verified = File.OpenRead(file))
            {
                Verify(verified);
                var start = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = directory.FullName };
                start.ArgumentList.Add("/silent");
                start.ArgumentList.Add("/install");
                process = Process.Start(start) ?? throw new IOException("Windows could not start Microsoft’s WebView2 installer.");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { throw new TimeoutException("Microsoft’s installer is still running. Wait for it to finish, then choose Check again."); }
            if (FindInstalledVersion() is null)
                throw new IOException($"Microsoft’s installer finished with code 0x{process.ExitCode:X8} and WebView2 is still missing. Check your connection or use Microsoft’s download page.");
        }
        finally
        {
            bool finished = process is null || process.HasExited;
            process?.Dispose();
            if (finished)
                try { File.Delete(file); directory.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static void VerifyEmbedded()
    {
        using var stream = OpenBootstrapper();
        Verify(stream);
    }

    private static Stream OpenBootstrapper() =>
        typeof(WebView2Prerequisite).Assembly.GetManifestResourceStream("WebView2Bootstrapper") ??
        throw new InvalidDataException("The embedded WebView2 installer is missing. Use Microsoft’s download page.");

    private static void Verify(Stream stream)
    {
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(BootstrapperSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The embedded WebView2 installer failed verification. Use Microsoft’s download page.");
    }
}
