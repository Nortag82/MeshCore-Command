using System.Net;
using System.Text;
using MeshCore.Windows.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MeshCore.Windows.Preview;

/// <summary>Counts selected settings in a disposable copy; never connects to a radio or prints values.</summary>
internal sealed class LegacyProfileAudit : Form
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcc-real-profile-audit-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _server = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _serverTask = Task.CompletedTask;
    private WebView2? _web;
    private bool _browserClosed = true;

    public LegacyProfileAudit()
    {
        Opacity = 0; ShowInTaskbar = false;
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string source = Path.Combine(local, "MeshCore WiFi Data", "WebView", "EBWebView");
            string data = Path.Combine(local, "MeshCore Command Data");
            NativeProfile profile = await NativeProfileRegistry.PrepareAsync(_root, source, data, port: 19483);
            string boardsPath = Path.Combine(profile.Data, "boards.json");
            int boards = new BoardProfiles(boardsPath).List().Count;
            byte[]? boardsHash = File.Exists(boardsPath) ? System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(boardsPath)) : null;
            _server.Prefixes.Add("http://127.0.0.1:19483/"); _server.Start(); _serverTask = ServeAsync();
            _browserClosed = false; _web = new WebView2 { Dock = DockStyle.Fill }; Controls.Add(_web);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile.WebView);
            await _web.EnsureCoreWebView2Async(environment);
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _web.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            _web.Source = new Uri("http://127.0.0.1:19483/");
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20))) throw new IOException("Copied profile could not be opened.");
            string countScript = """
                (()=>{let out={histories:0,messages:0,secureContacts:0,monitored:0,mapObjects:0,unreadable:0,credentialPairs:[],records:[]};
                  for(let i=0;i<localStorage.length;i++){const k=localStorage.key(i);try{
                    if(/^(?:rcc6-history-|meshcore-secure-v1:|meshcore-monitor-v1:|meshcore-situation-(?:v1|areas-v1):)[0-9a-f]{64}$/.test(k))out.records.push([k,localStorage.getItem(k)]);
                    if(/^rcc6-history-[0-9a-f]{64}$/.test(k)){const v=JSON.parse(localStorage.getItem(k));if(!Array.isArray(v))throw 0;out.histories++;out.messages+=v.length}
                    else if(/^meshcore-secure-v1:[0-9a-f]{64}$/.test(k)){const v=JSON.parse(localStorage.getItem(k));if(!v||!v.contacts||typeof v.contacts!=='object')throw 0;const owner=k.slice(-64),peers=Object.keys(v.contacts);out.secureContacts+=peers.length;for(const peer of peers){if(/^[0-9a-f]{64}$/.test(peer))out.credentialPairs.push([owner,peer]);else out.unreadable++}}
                    else if(/^meshcore-monitor-v1:[0-9a-f]{64}$/.test(k)){const v=JSON.parse(localStorage.getItem(k));if(!Array.isArray(v))throw 0;out.monitored+=v.length}
                    else if(/^meshcore-situation-(?:v1|areas-v1):[0-9a-f]{64}$/.test(k)){const v=JSON.parse(localStorage.getItem(k));if(!Array.isArray(v))throw 0;out.mapObjects+=v.length}
                  }catch{out.unreadable++}}out.records.sort((a,b)=>a[0].localeCompare(b[0]));return out})()
                """;
            string counts = await _web.CoreWebView2.ExecuteScriptAsync(countScript);
            using var result = System.Text.Json.JsonDocument.Parse(counts);
            var n = result.RootElement;
            int found = 0, missing = 0;
            foreach (var pair in n.GetProperty("credentialPairs").EnumerateArray())
            {
                string input = "secure-v1|" + pair[0].GetString() + "|" + pair[1].GetString();
                string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
                if (WindowsCredentials.Exists("secure", id)) found++; else missing++;
            }
            Console.WriteLine($"Copied profile: saved boards={boards}, history stores={n.GetProperty("histories").GetInt32()}, messages={n.GetProperty("messages").GetInt32()}, secure contacts={n.GetProperty("secureContacts").GetInt32()}, secure credentials present={found}, secure credentials missing={missing}, monitored entries={n.GetProperty("monitored").GetInt32()}, map objects={n.GetProperty("mapObjects").GetInt32()}, unreadable stores={n.GetProperty("unreadable").GetInt32()}.");
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnExit(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => exited.TrySetResult();
            environment.BrowserProcessExited += OnExit;
            Controls.Remove(_web); _web.Dispose(); _web = null;
            try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(20)); _browserClosed = true; }
            finally { environment.BrowserProcessExited -= OnExit; }
            _stop.Cancel(); _server.Close(); await _serverTask;

            // Open only the copied profile in the actual native shell, with radio access disabled.
            string executable = Environment.ProcessPath ?? throw new IOException("Audit executable is unavailable.");
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add("--audit-copied-profile"); start.ArgumentList.Add("--audit-root=" + _root);
            using (var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("Copied profile test could not start."))
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
                catch (TimeoutException) { child.Kill(entireProcessTree: true); throw; }
                if (child.ExitCode != 0) throw new IOException("Native shell could not open the copied profile.");
            }
            using var listener = new HttpListener(); listener.Prefixes.Add("http://127.0.0.1:19483/"); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            Task response = Task.Run(async () => {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                byte[] body = Encoding.UTF8.GetBytes("<!doctype html><title>Local profile audit</title>");
                context.Response.ContentType = "text/html"; context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, timeout.Token); context.Response.Close();
            });
            _browserClosed = false; _web = new WebView2 { Dock = DockStyle.Fill }; Controls.Add(_web);
            var afterEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile.WebView);
            await _web.EnsureCoreWebView2Async(afterEnvironment);
            var afterLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _web.CoreWebView2.NavigationCompleted += (_, e) => afterLoaded.TrySetResult(e.IsSuccess);
            _web.Source = new Uri("http://127.0.0.1:19483/");
            if (!await afterLoaded.Task.WaitAsync(timeout.Token)) throw new IOException("Copied profile could not be reopened.");
            await response;
            string afterCounts = await _web.CoreWebView2.ExecuteScriptAsync(countScript);
            using var after = System.Text.Json.JsonDocument.Parse(afterCounts);
            foreach (string key in new[] { "histories", "messages", "secureContacts", "monitored", "mapObjects", "unreadable" })
                if (n.GetProperty(key).GetInt32() != after.RootElement.GetProperty(key).GetInt32()) throw new InvalidDataException("Copied profile counts changed after opening the native shell.");
            if (n.GetProperty("records").GetRawText() != after.RootElement.GetProperty("records").GetRawText())
                throw new InvalidDataException("Copied profile records changed after opening the native shell.");
            byte[]? afterBoardsHash = File.Exists(boardsPath) ? System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(boardsPath)) : null;
            if (new BoardProfiles(boardsPath).List().Count != boards || (boardsHash is null) != (afterBoardsHash is null) ||
                (boardsHash is not null && afterBoardsHash is not null && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(boardsHash, afterBoardsHash)))
                throw new InvalidDataException("Saved boards changed after opening the native shell.");
            var afterExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnAfterExit(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => afterExited.TrySetResult();
            afterEnvironment.BrowserProcessExited += OnAfterExit;
            Controls.Remove(_web); _web.Dispose(); _web = null;
            try { await afterExited.Task.WaitAsync(TimeSpan.FromSeconds(20)); _browserClosed = true; }
            finally { afterEnvironment.BrowserProcessExited -= OnAfterExit; }
            Console.WriteLine("Native shell opened the copied profile; selected saved records and boards remained unchanged.");
        }
        catch (Exception error) { Console.Error.WriteLine("Profile audit failed: " + error.GetType().Name); Environment.ExitCode = 1; }
        finally
        {
            _web?.Dispose(); _stop.Cancel(); _server.Close();
            try { await _serverTask; } catch (OperationCanceledException) { } catch (HttpListenerException) { } catch (ObjectDisposedException) { } catch (IOException) { }
            _stop.Dispose();
            string target = Path.GetFullPath(_root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (_browserClosed && string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(target).StartsWith("mcc-real-profile-audit-", StringComparison.Ordinal) && Directory.Exists(target))
                try { Directory.Delete(target, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Temporary audit copy retained: " + target); Environment.ExitCode = 1; }
            else if (Directory.Exists(target)) { Console.Error.WriteLine("Temporary audit copy retained: " + target); Environment.ExitCode = 1; }
            Close();
        }
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
            byte[] body = Encoding.UTF8.GetBytes("<!doctype html><title>Local profile audit</title>");
            context.Response.ContentType = "text/html"; context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, _stop.Token); context.Response.Close();
        }
    }
}
