using System.Net;
using System.Net.Sockets;
using System.Text;
using MeshCore.Windows.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MeshCore.Windows.Preview;

/// <summary>Exercises a profile copy with synthetic localStorage in real WebView2 instances.</summary>
internal sealed class ProfileMigrationTest : Form
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcc-profile-integration-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _server = new();
    private CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private WebView2? _web;
    private string _url = "";

    public ProfileMigrationTest()
    {
        Opacity = 0; ShowInTaskbar = false;
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_root);
            // The app and the copied profile must use the same origin for localStorage.
            _url = "http://127.0.0.1:19484/"; _server.Prefixes.Add(_url);
            await StartListenerAsync(_server.Start);
            _loop = ServeAsync();
            string original = Path.Combine(_root, "original"), registry = Path.Combine(_root, "native-registry");
            string data = Path.Combine(_root, "data"); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "boards.json"), "{\"version\":1,\"boards\":[]}");
            var first = await OpenAsync(original);
            await NavigateAsync();
            await _web!.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('mcc-profile-test','synthetic-history'); localStorage.setItem('mcc-profile-secure-test','synthetic-secret-reference'); localStorage.setItem('mcc-profile-map-test','synthetic-map')");
            string historyKey = "rcc6-history-" + string.Concat(Enumerable.Repeat("07", 32));
            string history = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = "migrated-history-test", key = "ch:0", text = "synthetic staged history", timestamp = 1, direction = "in", status = "received" } });
            await _web.CoreWebView2.ExecuteScriptAsync("localStorage.setItem(" + System.Text.Json.JsonSerializer.Serialize(historyKey) + "," + System.Text.Json.JsonSerializer.Serialize(history) + ")");
            await CloseBrowserAsync(first);
            string sourceProfile = Path.Combine(original, "EBWebView");
            string sourceLock = Path.Combine(sourceProfile, "Default", "Local Storage", "leveldb", "LOCK");
            using (var held = new FileStream(sourceLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var blocked = new NativeCandidateStartup(registry, sourceProfile, data, port: 19484, hidden: true))
            {
                blocked.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (blocked.Working) await Task.Delay(50, deadline.Token);
                if (blocked.Profile is not null || !blocked.Status.Contains("Could not prepare", StringComparison.Ordinal) || File.Exists(Path.Combine(registry, "current.json")))
                    throw new InvalidDataException("Locked-profile startup did not fail safely.");
                blocked.Close();
            }
            NativeProfile staged;
            string syntheticCredential = new string('a', 64);
            var sourceCredentials = new PreviewCredentials(); sourceCredentials.Put("secure", syntheticCredential, "synthetic-secret");
            using (var startup = new NativeCandidateStartup(registry, Path.Combine(original, "EBWebView"), data, port: 19484, hidden: true, credentials: sourceCredentials))
            {
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startup.FormClosed += (_, _) => finished.TrySetResult();
                startup.Show();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(45));
                staged = startup.Profile ?? throw new InvalidDataException("Candidate startup did not select a migrated profile.");
            }
            string recovery = Directory.GetFiles(Path.Combine(registry, "recovery"), "*.mcccred").Single();
            if (Encoding.UTF8.GetString(await File.ReadAllBytesAsync(recovery)).Contains("synthetic-secret", StringComparison.Ordinal)) throw new InvalidDataException("Credential recovery was not protected.");
            var restoredCredentials = new PreviewCredentials();
            CredentialRecovery.Restore(recovery, restoredCredentials);
            if (restoredCredentials.Get("secure", syntheticCredential) != "synthetic-secret") throw new InvalidDataException("Credential recovery did not restore the saved secret.");
            var isolatedCredentials = new PreviewCredentials();
            await NativeCredentialIsolation.EnsureWithLegacyLockAsync(registry, Path.Combine(original, "EBWebView"), sourceCredentials, isolatedCredentials);
            if (isolatedCredentials.Get("secure", syntheticCredential) != "synthetic-secret") throw new InvalidDataException("Credential isolation did not copy the secret.");
            isolatedCredentials.Put("secure", syntheticCredential, "native-changed");
            await NativeCredentialIsolation.EnsureWithLegacyLockAsync(registry, Path.Combine(original, "EBWebView"), sourceCredentials, isolatedCredentials);
            if (isolatedCredentials.Get("secure", syntheticCredential) != "native-changed" || sourceCredentials.Get("secure", syntheticCredential) != "synthetic-secret")
                throw new InvalidDataException("Credential isolation did not preserve later independent changes.");
            string conflictRoot = Path.Combine(_root, "credential-conflict"); Directory.CreateDirectory(conflictRoot);
            var conflictCredentials = new PreviewCredentials(); conflictCredentials.Put("secure", syntheticCredential, "existing-other-secret");
            try { await NativeCredentialIsolation.EnsureWithLegacyLockAsync(conflictRoot, Path.Combine(original, "EBWebView"), sourceCredentials, conflictCredentials); throw new InvalidDataException("Credential conflict was accepted."); }
            catch (IOException) { }
            if (File.Exists(Path.Combine(conflictRoot, "credentials-isolated-v1")) || conflictCredentials.Get("secure", syntheticCredential) != "existing-other-secret")
                throw new InvalidDataException("Credential conflict changed data or committed the selection.");
            string lockedRoot = Path.Combine(_root, "credential-locked"); Directory.CreateDirectory(lockedRoot);
            string legacyLock = Path.Combine(original, "EBWebView", "Default", "Local Storage", "leveldb", "LOCK");
            using (var held = new FileStream(legacyLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lockedCredentials = new PreviewCredentials();
                try { await NativeCredentialIsolation.EnsureWithLegacyLockAsync(lockedRoot, Path.Combine(original, "EBWebView"), sourceCredentials, lockedCredentials); throw new InvalidDataException("Locked credential source was accepted."); }
                catch (IOException) { }
                if (File.Exists(Path.Combine(lockedRoot, "credentials-isolated-v1")) || lockedCredentials.Get("secure", syntheticCredential) is not null)
                    throw new InvalidDataException("Locked credential source changed the destination.");
            }
            string freshCredentialsRoot = Path.Combine(_root, "credential-fresh"); Directory.CreateDirectory(freshCredentialsRoot);
            var emptyLegacy = new PreviewCredentials(); var emptyNative = new PreviewCredentials();
            await NativeCredentialIsolation.EnsureWithLegacyLockAsync(freshCredentialsRoot, Path.Combine(_root, "missing-legacy"), emptyLegacy, emptyNative);
            if (!File.Exists(Path.Combine(freshCredentialsRoot, "credentials-isolated-v1"))) throw new InvalidDataException("Fresh credentials were not selected.");
            string resumedRoot = Path.Combine(_root, "credential-resume"); Directory.CreateDirectory(resumedRoot);
            var resumedCredentials = new PreviewCredentials(); resumedCredentials.Put("secure", syntheticCredential, "synthetic-secret");
            await NativeCredentialIsolation.EnsureWithLegacyLockAsync(resumedRoot, Path.Combine(original, "EBWebView"), sourceCredentials, resumedCredentials);
            if (!File.Exists(Path.Combine(resumedRoot, "credentials-isolated-v1")) || resumedCredentials.Get("secure", syntheticCredential) != "synthetic-secret")
                throw new InvalidDataException("Partial credential copy did not resume safely.");
            string windowsKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var windowsLegacy = new RestrictedCredentialStore(new WindowsBackupCredentials(), windowsKey);
            var windowsNative = new RestrictedCredentialStore(new WindowsBackupCredentials(native: true), windowsKey);
            bool safeToClean = false;
            try
            {
                if (windowsLegacy.Get("secure", windowsKey) is not null || windowsNative.Get("secure", windowsKey) is not null) throw new InvalidDataException("Test credential unexpectedly exists.");
                safeToClean = true;
                windowsLegacy.Put("secure", windowsKey, "synthetic-windows-secret");
                string windowsRoot = Path.Combine(_root, "credential-windows"); Directory.CreateDirectory(windowsRoot);
                await NativeCredentialIsolation.EnsureWithLegacyLockAsync(windowsRoot, Path.Combine(original, "EBWebView"), windowsLegacy, windowsNative);
                windowsNative.Put("secure", windowsKey, "native-changed");
                if (windowsLegacy.Get("secure", windowsKey) != "synthetic-windows-secret") throw new InvalidDataException("Native changes affected the old Windows credential.");
            }
            finally { if (safeToClean) { windowsNative.Delete("secure", windowsKey); windowsLegacy.Delete("secure", windowsKey); } }
            byte[] damaged = await File.ReadAllBytesAsync(recovery); damaged[^1] ^= 1;
            string damagedPath = Path.Combine(_root, "damaged.mcccred"); await File.WriteAllBytesAsync(damagedPath, damaged);
            restoredCredentials.Put("secure", syntheticCredential, "unchanged");
            try { CredentialRecovery.Restore(damagedPath, restoredCredentials); throw new InvalidDataException("Damaged credential recovery was accepted."); }
            catch (System.Security.Cryptography.CryptographicException) { }
            if (restoredCredentials.Get("secure", syntheticCredential) != "unchanged") throw new InvalidDataException("A damaged recovery changed credentials.");
            if (NativeProfileRegistry.Open(registry) != staged) throw new InvalidDataException("Could not reopen the selected native profile.");
            if (staged.Port != 19484) throw new InvalidDataException("The staged profile has the wrong local origin.");
            string copied = staged.WebView;
            _ = new BoardProfiles(Path.Combine(staged.Data, "boards.json"));
            var second = await OpenAsync(copied);
            await NavigateAsync();
            string result = await _web!.CoreWebView2.ExecuteScriptAsync("JSON.stringify([localStorage.getItem('mcc-profile-test'),localStorage.getItem('mcc-profile-secure-test'),localStorage.getItem('mcc-profile-map-test')])");
            string? storage = System.Text.Json.JsonSerializer.Deserialize<string>(result);
            if (storage != "[\"synthetic-history\",\"synthetic-secret-reference\",\"synthetic-map\"]") throw new InvalidDataException("Profile snapshot did not preserve localStorage.");
            await CloseBrowserAsync(second);
            _stop.Cancel(); _server.Close(); await _loop;
            using (var migrated = new PreviewWindow(false, false, false, true, null, 0, copied, staged.Data, seedHistory: false, port: staged.Port))
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                migrated.FormClosed += (_, _) => closed.TrySetResult();
                migrated.Show();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            }
            if (Environment.ExitCode != 0) throw new InvalidDataException("The native app did not display migrated history and a new message.");
            NativeProfile fresh;
            using (var freshStartup = new NativeCandidateStartup(Path.Combine(_root, "fresh-registry"), Path.Combine(_root, "missing-profile"), Path.Combine(_root, "missing-data"), port: 19484, hidden: true))
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                freshStartup.FormClosed += (_, _) => closed.TrySetResult();
                freshStartup.Show();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (freshStartup.Working) await Task.Delay(50, deadline.Token);
                if (freshStartup.Profile is not null || !freshStartup.Status.Contains("No previous", StringComparison.Ordinal)) throw new InvalidDataException("Fresh setup was not offered explicitly.");
                ((Button)freshStartup.Controls.Find("candidate-create-empty", true).Single()).PerformClick();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                fresh = freshStartup.Profile ?? throw new InvalidDataException("Fresh profile was not created.");
            }
            using (var freshApp = new PreviewWindow(false, false, false, true, null, 0, fresh.WebView, fresh.Data, seedHistory: true, port: fresh.Port))
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                freshApp.FormClosed += (_, _) => closed.TrySetResult();
                freshApp.Show();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            }
            if (Environment.ExitCode != 0) throw new InvalidDataException("The native app did not start with a fresh profile.");
            string executable = Environment.ProcessPath ?? throw new IOException("Test executable path is unavailable.");
            var sequenceStart = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                sequenceStart.ArgumentList.Add(FrameworkDependentEntryPoint());
            sequenceStart.ArgumentList.Add("--candidate-sequence-test"); sequenceStart.ArgumentList.Add("--test-root=" + _root);
            string receiptDir = Path.Combine(Path.GetTempPath(), "mcc-startup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(receiptDir);
            string receipt = Path.Combine(receiptDir, "ready");
            string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            sequenceStart.ArgumentList.Add("--startup-receipt=" + receipt); sequenceStart.ArgumentList.Add("--startup-nonce=" + nonce);
            try
            {
                using var sequence = System.Diagnostics.Process.Start(sequenceStart) ?? throw new IOException("Candidate sequence test could not start.");
                try
                {
                    await StartupHealthGate.WaitForProcessAsync(sequence, receipt, nonce, TimeSpan.FromSeconds(90));
                    await sequence.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
                }
                catch (TimeoutException) { sequence.Kill(entireProcessTree: true); throw; }
                if (sequence.ExitCode != 0) throw new InvalidDataException("Candidate startup and app sequence failed.");
            }
            finally
            {
                if (File.Exists(receipt)) File.Delete(receipt);
                if (Directory.Exists(receiptDir)) Directory.Delete(receiptDir);
            }
            Console.WriteLine("Synthetic WebView2 migration preserved localStorage, and the app displayed migrated history with a new message.");
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); Environment.ExitCode = 1; }
        finally
        {
            _web?.Dispose(); _stop.Cancel(); _server.Close();
            try { await _loop; } catch (OperationCanceledException) { } catch (HttpListenerException) { }
            _stop.Dispose();
            // The test owns this exact freshly generated directory under the temporary root.
            string temp = Path.GetFullPath(Path.GetTempPath());
            string target = Path.GetFullPath(_root);
            if (target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("mcc-profile-integration-", StringComparison.Ordinal))
                try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (IOException) { }
            Close();
        }
    }

    private async Task<CoreWebView2Environment> OpenAsync(string dataDirectory)
    {
        _web = new WebView2 { Dock = DockStyle.Fill }; Controls.Add(_web);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDirectory);
        await _web.EnsureCoreWebView2Async(environment);
        return environment;
    }

    private async Task NavigateAsync()
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) { _web!.CoreWebView2.NavigationCompleted -= Completed; loaded.TrySetResult(e.IsSuccess); }
        _web!.CoreWebView2.NavigationCompleted += Completed; _web.Source = new Uri(_url);
        if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new IOException("Test page did not load.");
    }

    private async Task CloseBrowserAsync(CoreWebView2Environment environment)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExit(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => exited.TrySetResult(true);
        environment.BrowserProcessExited += OnExit;
        Controls.Remove(_web!); _web!.Dispose(); _web = null;
        try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { environment.BrowserProcessExited -= OnExit; }
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _server.GetContextAsync().WaitAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { return; }
            byte[] body = Encoding.UTF8.GetBytes("<!doctype html><title>Profile test</title>");
            context.Response.ContentType = "text/html"; context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, _stop.Token); context.Response.Close();
        }
    }

    internal static async Task StartListenerAsync(Action start, Func<int, Task>? delay = null)
    {
        delay ??= milliseconds => Task.Delay(milliseconds);
        for (int attempt = 0; ; attempt++)
        {
            try { start(); return; }
            catch (HttpListenerException error) when (attempt < 20 && error.ErrorCode is 32 or 183)
            { await delay(250); }
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Only used when the current process is the dotnet host, not a single-file executable.")]
    private static string FrameworkDependentEntryPoint() => System.Reflection.Assembly.GetEntryAssembly()!.Location;

    private sealed class RestrictedCredentialStore(ICredentialStore inner, string allowedKey) : ICredentialStore
    {
        private void Check(string scope, string key) { if (scope != "secure" || key != allowedKey) throw new InvalidOperationException("Test credential is outside its scope."); }
        public string? Get(string scope, string key) { Check(scope, key); return inner.Get(scope, key); }
        public void Put(string scope, string key, string value) { Check(scope, key); inner.Put(scope, key, value); }
        public void Delete(string scope, string key) { Check(scope, key); inner.Delete(scope, key); }
        public IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null)
        {
            if (scope is not null || key is not null) Check(scope ?? "", key ?? "");
            return inner.ListSaved("secure", allowedKey);
        }
    }
}
