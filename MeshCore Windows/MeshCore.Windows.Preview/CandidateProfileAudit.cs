using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MeshCore.Windows.Preview;

/// <summary>Captures a digest of selected copied records without displaying their contents.</summary>
internal sealed class CandidateProfileAudit : Form
{
    internal const string CaptureScript = "JSON.stringify(Object.keys(localStorage).filter(k => !/^rcc6-(?:radio-owner|session)(?::|$)/.test(k)).sort().map(k => [k,localStorage.getItem(k)]))";
    private readonly string _profile;
    private readonly int _port;
    internal byte[]? Digest { get; private set; }
    internal Exception? Error { get; private set; }

    internal CandidateProfileAudit(string profile, int port)
    {
        _profile = profile; _port = port; Opacity = 0; ShowInTaskbar = false;
        Shown += async (_, _) => await CaptureAsync();
    }

    internal static byte[] Hash(string captured)
    {
        string json = System.Text.Json.JsonSerializer.Deserialize<string>(captured) ?? throw new InvalidDataException("Copied profile records could not be read.");
        using var records = System.Text.Json.JsonDocument.Parse(json);
        if (records.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) throw new InvalidDataException("Copied profile records are invalid.");
        foreach (var item in records.RootElement.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Array || item.GetArrayLength() != 2) throw new InvalidDataException("A copied profile record is invalid.");
            string key = item[0].GetString() ?? throw new InvalidDataException("A copied profile key is invalid.");
            string value = item[1].GetString() ?? throw new InvalidDataException("A copied profile record is missing.");
            bool secure = key.StartsWith("meshcore-secure-v1:", StringComparison.Ordinal);
            bool groups = key.StartsWith("meshcore-secure-groups-v1:", StringComparison.Ordinal);
            bool array = key.StartsWith("rcc6-history-", StringComparison.Ordinal) || key.StartsWith("meshcore-monitor-v1:", StringComparison.Ordinal) ||
                key.StartsWith("meshcore-situation-v1:", StringComparison.Ordinal) || key.StartsWith("meshcore-situation-areas-v1:", StringComparison.Ordinal);
            if (!secure && !groups && !array) continue;
            using var record = System.Text.Json.JsonDocument.Parse(value);
            if (secure)
            {
                if (record.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !record.RootElement.TryGetProperty("contacts", out var contacts) || contacts.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !record.RootElement.TryGetProperty("history", out var history) || history.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !record.RootElement.TryGetProperty("seen", out var seen) || seen.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException("A copied Secure Coms record is invalid.");
            }
            else if (groups)
            {
                if (record.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !record.RootElement.TryGetProperty("groups", out var savedGroups) || savedGroups.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !record.RootElement.TryGetProperty("history", out var groupHistory) || groupHistory.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException("A copied Secure Coms group record is invalid.");
            }
            else if (record.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidDataException("A copied profile record is invalid.");
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(captured));
    }

    private async Task CaptureAsync()
    {
        using var listener = new HttpListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        WebView2? web = null;
        try
        {
            string origin = $"http://127.0.0.1:{_port}/";
            listener.Prefixes.Add(origin); listener.Start();
            Task response = Task.Run(async () => {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                byte[] body = Encoding.UTF8.GetBytes("<!doctype html><title>Profile verification</title>");
                context.Response.ContentType = "text/html"; context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, deadline.Token); context.Response.Close();
            });
            web = new WebView2 { Dock = DockStyle.Fill }; Controls.Add(web);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            await web.EnsureCoreWebView2Async(environment);
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Source = new Uri(origin);
            if (!await loaded.Task.WaitAsync(deadline.Token)) throw new IOException("Copied profile could not be inspected.");
            await response;
            Digest = Hash(await web.CoreWebView2.ExecuteScriptAsync(CaptureScript));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnExit(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => exited.TrySetResult();
            environment.BrowserProcessExited += OnExit;
            Controls.Remove(web); web.Dispose(); web = null;
            try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { environment.BrowserProcessExited -= OnExit; }
        }
        catch (Exception error) { Error = error; Digest = null; }
        finally { web?.Dispose(); Close(); }
    }
}
