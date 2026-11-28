using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MeshCore.Windows.Core;

/// <summary>Authenticated loopback frame endpoints for independent boards.</summary>
public sealed class BoardFrameEndpoint : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ICompanionBoard> _boards = new();
    private readonly Task _loop;
    private readonly byte[] _token;
    private readonly byte[]? _shell;
    private readonly byte[]? _contactMap;
    private readonly string _contactMapPath = $"/hflink/{Guid.NewGuid():N}/";
    private readonly string? _boardHtml;
    private readonly IBackupCredentials? _credentials;
    private readonly OfflineMapStore? _offlineMap;
    private readonly ElevationService? _elevation;
    private readonly Action<string, string>? _notify;
    private readonly OfflineDownloads? _downloads;
    private string? _firstBoard;
    public string Origin { get; }
    public string Token { get; }
    public string HfContactsMapUrl => Origin + _contactMapPath;

    public BoardFrameEndpoint(int port, byte[]? shell = null, string? boardHtml = null, IBackupCredentials? credentials = null, OfflineMapStore? offlineMap = null, ElevationService? elevation = null, Action<string, string>? notify = null, OfflineDownloads? downloads = null, byte[]? contactMap = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Origin = $"http://127.0.0.1:{port}";
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _token = Encoding.ASCII.GetBytes(Token);
        _shell = shell;
        _contactMap = contactMap;
        _boardHtml = boardHtml;
        _credentials = credentials;
        _offlineMap = offlineMap;
        _elevation = elevation;
        _notify = notify;
        _downloads = downloads;
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _loop = LoopAsync();
    }

    public void AddBoard(string id, ICompanionBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{32}$")) throw new ArgumentException("Invalid board ID.", nameof(id));
        if (!_boards.TryAdd(id, board)) throw new InvalidOperationException("Board already exists.");
        Interlocked.CompareExchange(ref _firstBoard, id, null);
    }

    public bool RemoveBoard(string id) => _boards.TryRemove(id, out _);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            var path = req.Url?.AbsolutePath ?? "";
            if (!string.Equals(req.UserHostName, new Uri(Origin).Authority, StringComparison.OrdinalIgnoreCase)) { response.StatusCode = 403; return; }
            if (req.HttpMethod == "GET" && path == "/" && _shell is not null)
            {
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = _shell.Length;
                await response.OutputStream.WriteAsync(_shell, _stop.Token);
                return;
            }
            if (_contactMap is not null && req.HttpMethod == "GET" && path.StartsWith(_contactMapPath, StringComparison.Ordinal))
            {
                string resource = path[_contactMapPath.Length..];
                if (resource.Length == 0)
                {
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = _contactMap.Length;
                    await response.OutputStream.WriteAsync(_contactMap, _stop.Token);
                }
                else if (resource is "offline-map" or "offline-map.pmtiles" ||
                    System.Text.RegularExpressions.Regex.IsMatch(resource, @"\Aoffline-tile/[0-9]{1,2}/[0-9]+/[0-9]+\z"))
                    await ServeOfflineMapAsync(req, response, resource);
                else response.StatusCode = 404;
                return;
            }
            var view = System.Text.RegularExpressions.Regex.Match(path, "^/board/([0-9a-f]{32})/$");
            if (req.HttpMethod == "GET" && view.Success && _boardHtml is not null && _boards.ContainsKey(view.Groups[1].Value))
            {
                string id = view.Groups[1].Value;
                string script = "<script>(()=>{const id=" + System.Text.Json.JsonSerializer.Serialize(id) + ",token=" + System.Text.Json.JsonSerializer.Serialize(Token) + ";window.__MCC_BOARD_ID=id;window.__MCC_BOARD_FIRST=true;window.__MCC_BOARD_ACTIVE=false;window.__MCC_NOTIFY_BOARD_MESSAGE=()=>{if(!window.__MCC_BOARD_ACTIVE&&parent!==window)parent.postMessage({type:'mcc-board-message',id},location.origin)};window.addEventListener('message',e=>{if(e.source===parent&&e.origin===location.origin&&e.data?.type==='mcc-board-active'&&e.data.id===id){window.__MCC_BOARD_ACTIVE=e.data.active===true;window.dispatchEvent(new Event('mcc-board-active-change'))}});const original=window.fetch.bind(window);window.fetch=(input,options={})=>{if(typeof input==='string'&&input.startsWith('/api/')){const headers=new Headers(options.headers||{});headers.set('X-MCC-Board-Token',token);return original('/board/'+id+input,{...options,headers})}return original(input,options)};function api(){try{if(parent!==window&&parent.pywebview){window.pywebview=parent.pywebview;window.dispatchEvent(new Event('pywebviewready'))}}catch{}}if(parent!==window){parent.addEventListener('pywebviewready',api);api()}})();</script>";
                script = script.Replace("window.__MCC_BOARD_FIRST=true;", "window.__MCC_BOARD_FIRST=" + (id == _firstBoard ? "true" : "false") + ";", StringComparison.Ordinal);
                if (!_boardHtml.Contains("</head>", StringComparison.Ordinal)) { response.StatusCode = 500; return; }
                byte[] html = Encoding.UTF8.GetBytes(_boardHtml.Replace("</head>", "<style>#window-controls{display:none!important}</style>" + script + "</head>", StringComparison.Ordinal));
                response.ContentType = "text/html; charset=utf-8"; response.ContentLength64 = html.Length;
                await response.OutputStream.WriteAsync(html, _stop.Token);
                return;
            }
            var match = System.Text.RegularExpressions.Regex.Match(path, "^/board/([0-9a-f]{32})/api/(frame|secure-credential|repeater-credential|notify|elevation|offline-download|offline-map|offline-map\\.pmtiles|offline-tile/[0-9]{1,2}/[0-9]+/[0-9]+)$");
            if (!match.Success || !_boards.TryGetValue(match.Groups[1].Value, out var board)) { response.StatusCode = 404; return; }
            var supplied = req.Headers["X-MCC-Board-Token"] ?? "";
            byte[] candidate = Encoding.ASCII.GetBytes(supplied);
            string operation = match.Groups[2].Value;
            bool publicTile = req.HttpMethod == "GET" && operation.StartsWith("offline-tile/", StringComparison.Ordinal);
            if (!string.Equals(req.UserHostName, new Uri(Origin).Authority, StringComparison.OrdinalIgnoreCase) ||
                (!publicTile && (candidate.Length != _token.Length || !CryptographicOperations.FixedTimeEquals(candidate, _token))))
            { response.StatusCode = 403; return; }
            if (operation == "offline-download")
            {
                if (_downloads is null) { response.StatusCode = 501; return; }
                if (req.HttpMethod == "GET")
                {
                    var info = _downloads.Status;
                    byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { status = info.Status, message = info.Message, name = info.Name });
                    response.ContentType = "application/json"; response.ContentLength64 = json.Length;
                    await response.OutputStream.WriteAsync(json, _stop.Token); return;
                }
                if (req.HttpMethod != "POST") { response.StatusCode = 405; return; }
                if (req.Headers["Origin"] != Origin) { response.StatusCode = 403; return; }
                if (req.ContentLength64 is < 1 or > 1024) { response.StatusCode = 400; return; }
                byte[] request = new byte[(int)req.ContentLength64]; await req.InputStream.ReadExactlyAsync(request, _stop.Token);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(request);
                    var root = doc.RootElement;
                    double[] bounds = root.GetProperty("bounds").EnumerateArray().Select(item => item.GetDouble()).ToArray();
                    _downloads.Start(bounds, root.GetProperty("zoom").GetInt32());
                    byte[] json = "{\"started\":true}"u8.ToArray(); response.ContentType = "application/json"; response.ContentLength64 = json.Length;
                    await response.OutputStream.WriteAsync(json, _stop.Token);
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { response.StatusCode = 400; }
                return;
            }
            if (operation == "notify")
            {
                if (_notify is null) { response.StatusCode = 501; return; }
                if (req.HttpMethod != "POST") { response.StatusCode = 405; return; }
                if (req.Headers["Origin"] != Origin) { response.StatusCode = 403; return; }
                if (req.ContentLength64 is < 1 or > 4096) { response.StatusCode = 400; return; }
                byte[] request = new byte[(int)req.ContentLength64]; await req.InputStream.ReadExactlyAsync(request, _stop.Token);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(request);
                    string? title = doc.RootElement.GetProperty("title").GetString(), body = doc.RootElement.GetProperty("body").GetString();
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) { response.StatusCode = 400; return; }
                    title = title[..Math.Min(title.Length, 63)].Replace("\0", ""); body = body[..Math.Min(body.Length, 255)].Replace("\0", "");
                    _notify(title, body);
                    byte[] json = "{\"queued\":true}"u8.ToArray(); response.ContentType = "application/json"; response.ContentLength64 = json.Length;
                    await response.OutputStream.WriteAsync(json, _stop.Token);
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { response.StatusCode = 400; }
                return;
            }
            if (operation == "elevation")
            {
                if (_elevation is null) { response.StatusCode = 501; return; }
                if (req.HttpMethod != "POST") { response.StatusCode = 405; return; }
                if (req.Headers["Origin"] != Origin) { response.StatusCode = 403; return; }
                if (req.ContentLength64 is < 1 or > 12000) { response.StatusCode = 400; return; }
                byte[] request = new byte[(int)req.ContentLength64]; await req.InputStream.ReadExactlyAsync(request, _stop.Token);
                try
                {
                    var values = await _elevation.QueryAsync(request, _stop.Token);
                    byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { elevation = values });
                    response.ContentType = "application/json"; response.ContentLength64 = json.Length;
                    await response.OutputStream.WriteAsync(json, _stop.Token);
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { response.StatusCode = 400; }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or OperationCanceledException) { response.StatusCode = 502; }
                return;
            }
            if (operation == "offline-map" || operation == "offline-map.pmtiles" || publicTile)
            {
                await ServeOfflineMapAsync(req, response, operation);
                return;
            }
            if (operation != "frame")
            {
                if (_credentials is null) { response.StatusCode = 501; return; }
                if (req.HttpMethod != "POST") { response.StatusCode = 405; return; }
                if (req.Headers["Origin"] != Origin) { response.StatusCode = 403; return; }
                if (req.ContentLength64 is < 1 or > 4096) { response.StatusCode = 400; return; }
                byte[] request = new byte[(int)req.ContentLength64];
                await req.InputStream.ReadExactlyAsync(request, _stop.Token);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(request);
                    var root = doc.RootElement;
                    string? key = root.GetProperty("key").GetString(), action = root.GetProperty("action").GetString();
                    if (key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[0-9a-f]{64}$")) { response.StatusCode = 400; return; }
                    string scope = operation == "secure-credential" ? "secure" : "repeater";
                    object result;
                    if (action == "get") result = new { password = _credentials.Get(scope, key) };
                    else if (action == "delete") { _credentials.Delete(scope, key); result = new { ok = true }; }
                    else if (action == "save")
                    {
                        string? password = root.GetProperty("password").GetString();
                        if (password is null || Encoding.UTF8.GetByteCount(password) is < 1 or > 512) { response.StatusCode = 400; return; }
                        _credentials.Put(scope, key, password); result = new { ok = true };
                    }
                    else { response.StatusCode = 400; return; }
                    byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result);
                    response.ContentType = "application/json"; response.ContentLength64 = json.Length;
                    await response.OutputStream.WriteAsync(json, _stop.Token);
                }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
                { response.StatusCode = 400; }
                catch (System.ComponentModel.Win32Exception) { response.StatusCode = 503; }
                return;
            }
            if (req.HttpMethod == "GET")
            {
                if (!board.Connected) { response.StatusCode = 503; return; }
                uint? ack = uint.TryParse(req.Headers["X-RCC6-Ack"], NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed) ? parsed : null;
                var frame = board.Received.PeekAfterAcknowledgement(ack);
                if (frame is null) { response.StatusCode = 204; return; }
                response.Headers["X-RCC6-Seq"] = frame.Sequence.ToString(CultureInfo.InvariantCulture);
                response.ContentType = "application/octet-stream";
                response.ContentLength64 = frame.Payload.Length;
                await response.OutputStream.WriteAsync(frame.Payload, _stop.Token);
                return;
            }
            if (req.HttpMethod != "POST") { response.StatusCode = 405; return; }
            if (req.Headers["Origin"] != Origin) { response.StatusCode = 403; return; }
            if (req.ContentLength64 is < 1 or > CompanionFrames.MaximumPayloadLength) { response.StatusCode = 400; return; }
            var payload = new byte[(int)req.ContentLength64];
            await req.InputStream.ReadExactlyAsync(payload, _stop.Token);
            await board.SendAsync(payload, _stop.Token);
            response.StatusCode = 200;
        }
        catch (Exception e) when (e is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException)
        { try { response.StatusCode = 503; } catch (ObjectDisposedException) { } }
        finally { response.Close(); }
    }

    private async Task ServeOfflineMapAsync(HttpListenerRequest req, HttpListenerResponse response, string operation)
    {
        if (_offlineMap is null || req.HttpMethod != "GET") { response.StatusCode = 404; return; }
        try
        {
            if (operation == "offline-map")
            {
                var info = _offlineMap.Info;
                byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { available = info.Available, name = info.Name, format = info.Format, maxZoom = info.MaxZoom });
                response.ContentType = "application/json"; response.ContentLength64 = json.Length;
                await response.OutputStream.WriteAsync(json, _stop.Token); return;
            }
            if (operation.StartsWith("offline-tile/", StringComparison.Ordinal))
            {
                string[] parts = operation.Split('/');
                if (!int.TryParse(parts[1], out int z) || !long.TryParse(parts[2], out long x) || !long.TryParse(parts[3], out long y)) { response.StatusCode = 404; return; }
                var tile = _offlineMap.ReadTile(z, x, y);
                if (tile is null) { response.StatusCode = 404; return; }
                response.ContentType = tile.ContentType; response.ContentLength64 = tile.Data.Length;
                await response.OutputStream.WriteAsync(tile.Data, _stop.Token); return;
            }
            var range = System.Text.RegularExpressions.Regex.Match(req.Headers["Range"] ?? "", "^bytes=([0-9]+)-([0-9]*)$");
            if (!range.Success || !long.TryParse(range.Groups[1].Value, out long start) || (range.Groups[2].Length > 0 && !long.TryParse(range.Groups[2].Value, out _))) { response.StatusCode = 416; return; }
            long? end = range.Groups[2].Length == 0 ? null : long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            var data = _offlineMap.ReadRange(start, end);
            if (data is null) { response.StatusCode = 416; return; }
            response.StatusCode = 206; response.ContentType = "application/octet-stream";
            response.Headers["Content-Range"] = $"bytes {start}-{data.Value.End}/{data.Value.Size}";
            response.Headers["Accept-Ranges"] = "bytes"; response.ContentLength64 = data.Value.Data.Length;
            await response.OutputStream.WriteAsync(data.Value.Data, _stop.Token); return;
        }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException or ArgumentException) { response.StatusCode = 404; return; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        await _loop;
        _stop.Dispose();
    }
}
