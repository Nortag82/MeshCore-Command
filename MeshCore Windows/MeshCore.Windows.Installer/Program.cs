using MeshCore.Windows.Core;
using System.Reflection;

namespace MeshCore.Windows.Installer;

internal static class Program
{
    // This isolated installer is deliberately separate from the existing production app.
    internal static readonly string Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? throw new InvalidOperationException("Installer version is missing.");
    private const string ApplicationName = "MeshCore.Windows.Preview.exe";
    private const string LauncherName = "MeshCore Command Native Preview.exe";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--test-update"))
        {
            string root = Path.Combine(Path.GetTempPath(), "mcc-native-update-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (Version == "0.0.0") throw new InvalidOperationException("The test needs a version newer than 0.0.0.");
                InstallAsync(root, new Progress<string>(), version: "0.0.0").GetAwaiter().GetResult();
                string previous = VersionedInstall.ResolveCurrent(root);
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Milliseconds 700");
                using var parent = System.Diagnostics.Process.Start(start) ?? throw new IOException("Update test process could not start.");
                long parentStart = parent.StartTime.ToUniversalTime().Ticks;
                var rejectedStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                rejectedStart.ArgumentList.Add("--test-update-child"); rejectedStart.ArgumentList.Add("--test-root=" + root);
                rejectedStart.ArgumentList.Add("--parent-pid=" + parent.Id); rejectedStart.ArgumentList.Add("--parent-start=" + parentStart);
                rejectedStart.ArgumentList.Add("--expected-version=999.999.999");
                using (var rejected = System.Diagnostics.Process.Start(rejectedStart) ?? throw new IOException("Update rejection test could not start."))
                {
                    if (!rejected.WaitForExit(30000)) { rejected.Kill(entireProcessTree: true); throw new TimeoutException("Update rejection test timed out."); }
                    if (rejected.ExitCode == 0 || VersionedInstall.ResolveCurrent(root) != previous) throw new IOException("Mismatched update version was not rejected safely.");
                }
                using var fakeHttp = new HttpClient(new InstallerTestReleaseHandler(Environment.ProcessPath!, Version));
                var childStart = UpdateHandoff.PrepareAsync(new UpdaterClient(fakeHttp), "0.0.0", Version, Path.Combine(root, "downloads"), parent.Id, parentStart).GetAwaiter().GetResult();
                childStart.ArgumentList[0] = "--test-update-child"; childStart.ArgumentList.Add("--test-root=" + root); childStart.CreateNoWindow = true;
                using var heldProfile = NativeInstanceLease.Acquire(Path.Combine(root, "test-profile"));
                using var updater = System.Diagnostics.Process.Start(childStart) ?? throw new IOException("Update test installer could not start.");
                Thread.Sleep(1500);
                if (updater.HasExited || VersionedInstall.ResolveCurrent(root) != previous) throw new IOException("Update did not wait for the profile lock.");
                heldProfile.Dispose();
                if (!updater.WaitForExit(120000)) { updater.Kill(entireProcessTree: true); throw new TimeoutException("Update test installer did not finish."); }
                if (updater.ExitCode != 0) throw new IOException("Update test installer failed.");
                if (!parent.HasExited || !File.Exists(previous) || !VersionedInstall.ResolveCurrent(root).Contains(Path.DirectorySeparatorChar + Version + Path.DirectorySeparatorChar))
                    throw new IOException("Isolated update did not preserve and activate versions correctly.");
            }
            catch { Environment.ExitCode = 1; }
            finally
            {
                string target = Path.GetFullPath(root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("mcc-native-update-test-", StringComparison.Ordinal) && Directory.Exists(target))
                    try { Directory.Delete(target, recursive: true); } catch (IOException) { }
            }
            return;
        }
        if (args.Contains("--test-update-child"))
        {
            try
            {
                string? rootArg = args.FirstOrDefault(value => value.StartsWith("--test-root=", StringComparison.Ordinal));
                string root = Path.GetFullPath(rootArg?[12..] ?? throw new ArgumentException("Missing test root."));
                string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("mcc-native-update-test-", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid test root.");
                var handoff = ParseHandoff(args);
                UpdateAsync(root, handoff.Pid, handoff.Start, launch: true, testLaunch: true).GetAwaiter().GetResult();
            }
            catch { Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--test-install"))
        {
            string root = Path.Combine(Path.GetTempPath(), "mcc-native-installer-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                InstallAsync(root, new Progress<string>()).GetAwaiter().GetResult();
                string executable = VersionedInstall.ResolveCurrent(root);
                if (!File.Exists(executable) || !File.Exists(Path.Combine(root, LauncherName))) throw new IOException("Installer test did not create both executables.");
                string shortcuts = Path.Combine(root, "test-programs"); Directory.CreateDirectory(shortcuts);
                string link = PreviewShortcut.Create(shortcuts, Path.Combine(root, LauncherName));
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                object resolved = ((dynamic)shell).CreateShortcut(link);
                try
                {
                    if (!string.Equals((string)((dynamic)resolved).TargetPath, Path.Combine(root, LauncherName), StringComparison.OrdinalIgnoreCase) ||
                        (string)((dynamic)resolved).Arguments != PreviewShortcut.Arguments)
                        throw new IOException("Shortcut does not open the native candidate.");
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(resolved); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                byte[] original = File.ReadAllBytes(link);
                try { PreviewShortcut.Create(shortcuts, Path.Combine(root, LauncherName)); throw new InvalidOperationException("Existing shortcut was replaced."); }
                catch (IOException) { }
                if (!File.ReadAllBytes(link).SequenceEqual(original)) throw new IOException("Existing shortcut was modified.");
                using var app = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(root, LauncherName)) {
                    WorkingDirectory = root, UseShellExecute = false,
                    CreateNoWindow = true, Arguments = "--installer-test --integration-test" }) ?? throw new IOException("Installed test app did not start.");
                if (!app.WaitForExit(60000)) { app.Kill(entireProcessTree: true); throw new TimeoutException("Installed test app did not finish."); }
                if (app.ExitCode != 0) throw new IOException("Installed test app did not pass its integration test.");
            }
            catch { Environment.ExitCode = 1; }
            finally
            {
                // Only the freshly generated direct child of the Windows temp directory is removed.
                string absolute = Path.GetFullPath(root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(absolute).StartsWith("mcc-native-installer-test-", StringComparison.Ordinal) && Directory.Exists(absolute))
                    try { Directory.Delete(absolute, recursive: true); } catch (IOException) { }
            }
            return;
        }
        if (args.Contains("--native-update"))
        {
            try
            {
                var handoff = ParseHandoff(args);
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
                UpdateAsync(root, handoff.Pid, handoff.Start, launch: true).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                MessageBox.Show("The native update could not finish.\n\n" + error.Message, "MeshCore Command update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerWindow());
    }

    internal static async Task InstallAsync(string root, IProgress<string> progress, CancellationToken cancellationToken = default, string? version = null)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (target.Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid install directory.");
        Directory.CreateDirectory(target);
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("The installation directory cannot be a link.");
        string temporary = Path.Combine(target, ".payload-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            progress.Report("Preparing the embedded preview package…");
            await using (Stream payload = Resource("InstallerPayload"))
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            { await payload.CopyToAsync(file, cancellationToken); await file.FlushAsync(cancellationToken); }
            progress.Report("Verifying application files…");
            string staged = await InstallPackage.StageAsync(temporary, Path.Combine(target, "staging"), cancellationToken);
            string launcher = Path.Combine(target, LauncherName);
            if (!File.Exists(launcher))
            {
                string tempLauncher = Path.Combine(target, ".launcher-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (Stream source = Resource("InstallerLauncher"))
                    await using (var output = new FileStream(tempLauncher, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    { await source.CopyToAsync(output, cancellationToken); await output.FlushAsync(cancellationToken); }
                    File.Move(tempLauncher, launcher, overwrite: false);
                }
                finally { if (File.Exists(tempLauncher)) File.Delete(tempLauncher); }
            }
            else if ((File.GetAttributes(launcher) & FileAttributes.ReparsePoint) != 0) throw new IOException("The existing launcher cannot be a link.");
            progress.Report("Activating the preview…");
            await VersionedInstall.ActivateAsync(target, staged, version ?? Version, ApplicationName, cancellationToken);
            progress.Report("Preview installed. The existing MeshCore Command installation was not changed.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task UpdateAsync(string root, int parentPid, long parentStart, bool launch, CancellationToken cancellationToken = default, bool testLaunch = false)
    {
        await UpdateParentGate.WaitAsync(parentPid, parentStart, TimeSpan.FromSeconds(60), cancellationToken);
        string profileRoot = testLaunch || !launch ? Path.Combine(root, "test-profile") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Candidate Data");
        string previous = VersionedInstall.ResolveCurrent(root);
        using (await NativeInstanceLease.WaitAsync(profileRoot, TimeSpan.FromSeconds(60), cancellationToken))
            await InstallAsync(root, new Progress<string>(), cancellationToken);
        string activated = Path.Combine(Path.GetFullPath(root), "versions", Version, ApplicationName);
        if (launch)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, LauncherName)) { WorkingDirectory = root, UseShellExecute = false };
                if (testLaunch) { start.ArgumentList.Add("--installer-test"); start.ArgumentList.Add("--integration-test"); }
                else start.ArgumentList.Add("--native-candidate");
                using var launched = System.Diagnostics.Process.Start(start) ?? throw new IOException("The updated app could not start.");
                if (testLaunch)
                {
                    await launched.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
                    if (launched.ExitCode != 0) throw new IOException("The updated test app did not pass its integration test.");
                }
            }
            catch (Exception error)
            {
                try
                {
                    if (!await VersionedInstall.RollbackIfUnchangedAsync(root, activated, previous, CancellationToken.None))
                        throw new IOException("Installation changed during the failed update; automatic rollback was not attempted.");
                }
                catch (Exception rollback) { throw new AggregateException("Update launch failed and automatic rollback could not finish.", error, rollback); }
                throw new IOException("Update launch failed; the previous version was restored.", error);
            }
        }
    }

    private static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException("Installer resource is missing.");

    private static (int Pid, long Start) ParseHandoff(string[] args)
    {
        string? pidArg = args.FirstOrDefault(value => value.StartsWith("--parent-pid=", StringComparison.Ordinal));
        string? startArg = args.FirstOrDefault(value => value.StartsWith("--parent-start=", StringComparison.Ordinal));
        string? versionArg = args.FirstOrDefault(value => value.StartsWith("--expected-version=", StringComparison.Ordinal));
        if (!int.TryParse(pidArg?[13..], out int pid) || !long.TryParse(startArg?[15..], out long start) || versionArg?[19..] != Version)
            throw new ArgumentException("Invalid update handoff or installer version.");
        return (pid, start);
    }
}

internal sealed class InstallerTestReleaseHandler(string executable, string version) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "api.github.com")
        {
            using var input = File.OpenRead(executable);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
            string name = $"MeshCore-Command-Native-Windows-{version}-Setup.exe";
            var body = new[] { new { tag_name = "v" + version, published_at = "2026-01-01T00:00:00Z", assets = new[] { new { name, state = "uploaded", size = input.Length, digest = "sha256:" + hash, browser_download_url = $"https://github.com/Nortag82/MeshCore-Command/releases/download/v{version}/{name}" } } } };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body)) });
        }
        if (request.RequestUri?.Host == "github.com")
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(executable)) });
        throw new InvalidDataException("Unexpected update test URL.");
    }
}

internal sealed class InstallerWindow : Form
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(450, 0), Text = "Development preview: this uses a separate installation and test data." };
    private readonly Button _install = new() { Text = "Install preview", AutoSize = true };
    private readonly Button _launch = new() { Text = "Open preview", AutoSize = true, Visible = false };
    private readonly Button _candidate = new() { Text = "Open native candidate", AutoSize = true, Visible = false };
    private readonly Button _shortcut = new() { Text = "Create Start menu shortcut", AutoSize = true, Visible = false };
    private bool _busy;
    public InstallerWindow()
    {
        Text = "MeshCore Command — native preview setup"; Width = 520; Height = 320; StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(18), WrapContents = false };
        layout.Controls.Add(new Label { Text = "MeshCore Command native preview", AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) });
        layout.Controls.Add(_status); layout.Controls.Add(_install); layout.Controls.Add(_launch); layout.Controls.Add(_candidate); layout.Controls.Add(_shortcut); Controls.Add(layout);
        _install.Click += async (_, _) => {
            if (_busy) return;
            _busy = true; _install.Enabled = false;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
                await Program.InstallAsync(root, new Progress<string>(message => _status.Text = message));
                _launch.Visible = true; _candidate.Visible = true; _shortcut.Visible = true;
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally { _busy = false; _install.Enabled = !_launch.Visible; }
        };
        _launch.Click += (_, _) => {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "MeshCore Command Native Preview.exe")) { WorkingDirectory = root, UseShellExecute = false })?.Dispose();
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        _candidate.Click += (_, _) => {
            if (MessageBox.Show(this, "The development candidate uses separate copies of your existing settings, messages and credentials. Later changes are not merged with the current app. Close the current app before continuing. Open the candidate?", "Native candidate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "MeshCore Command Native Preview.exe")) { WorkingDirectory = root, UseShellExecute = false };
                start.ArgumentList.Add("--native-candidate");
                using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("The candidate could not be started.");
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        _shortcut.Click += (_, _) => {
            if (MessageBox.Show(this, "This shortcut opens the experimental native candidate. It may copy your existing profile and credentials; later changes are not merged with the old app. Close the current app before using it. Create the shortcut?", "Native candidate shortcut", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
                _ = PreviewShortcut.Create(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Path.Combine(root, "MeshCore Command Native Preview.exe"));
                _status.Text = "Start menu shortcut created for the native candidate.";
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Please wait for installation to finish."; } };
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCore Command Native Preview");
            string current = VersionedInstall.ResolveCurrent(root);
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(current)), Program.Version, StringComparison.Ordinal) &&
                string.Equals(Path.GetFileName(current), "MeshCore.Windows.Preview.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(root, "MeshCore Command Native Preview.exe")))
            { _status.Text = "A native preview is already installed. Open it, or install a newer preview when available."; _install.Enabled = false; _launch.Visible = true; _candidate.Visible = true; _shortcut.Visible = true; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { }
    }
}
