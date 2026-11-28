using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal sealed class HfJs8View : UserControl
{
    private const int BandActivityLimit = 250;
    private readonly HfLink _store;
    private readonly Js8ManagedModem _modem = new();
    private readonly HfWaterfallView _waterfall = new();
    private readonly RowStyle _waterfallRow = new(SizeType.Absolute, 162);
    private bool _connectAllowed;
    private readonly Action<HfMessage> _received;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _inbox = new("Inbox"), _public = new("Public"), _contacts = new("Contacts"), _history = new("Earlier HFLink history");
    private readonly TabPage _band = new("Band activity");
    private readonly DataGridView _inboxRows = Grid("Time", "From", "To", "Status", "Message");
    private readonly DataGridView _publicRows = Grid("Time", "From", "To", "Status", "Message");
    private readonly DataGridView _contactRows = Grid("Callsign", "Last heard", "SNR", "Grid locator");
    private readonly DataGridView _historyRows = Grid("Time", "From", "To", "Status", "Message");
    private readonly DataGridView _bandRows = Grid("Time", "Dial", "Offset", "SNR", "Decoded text");
    private int _bandRowsToShow = BandActivityLimit;
    private readonly TextBox _destination = new() { Width = 120, MaxLength = 15, AccessibleName = "Destination callsign" };
    private readonly TextBox _draft = new() { Width = 400, MaxLength = 180, AccessibleName = "JS8 message" };
    private readonly TextBox _broadcastDraft = new() { Width = 400, MaxLength = 180, AccessibleName = "Broadcast message" };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _activity = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _target = new() { Text = "To", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _send, _broadcast, _leaveAtRelay, _checkRelay, _retrieve;
    private readonly Button _installJs8, _waterfallToggle, _stopTx, _dial;
    private readonly Func<IReadOnlyList<HfHeardStation>, Task>? _showContactMap;
    private readonly Dictionary<string, int> _snr = new(StringComparer.Ordinal);
    private Js8LinkSettings _settings;
    private CancellationTokenSource? _lifetime;
    private Task? _connection;
    private Js8CallClient? _client;
    private CancellationTokenSource? _installerDownload;
    private string _callsign = "";
    private bool _sending;
    private bool _js8Installed;
    private bool _showWaterfall = true;
    private bool _waterfallStarted;
    private int? _relayMessageId;

    public HfJs8View(string directory, bool connectAllowed, Func<Task> returnToRadio, Action<HfMessage> received,
        Func<IReadOnlyList<HfHeardStation>, Task>? showContactMap = null)
    {
        _store = new HfLink(Path.Combine(directory, "hflink-native.sqlite3"), stationSimulation: false);
        _settings = _store.Js8Settings();
        var drafts = _store.LoadJs8Drafts();
        _destination.Text = drafts.Destination;
        _draft.Text = drafts.Direct;
        _broadcastDraft.Text = drafts.Broadcast;
        string? phy = Js8PhyLab.Executable();
        _connectAllowed = connectAllowed && phy is null;
        _received = received;
        _showContactMap = showContactMap;
        Dock = DockStyle.Fill;
        AccessibleName = "HFLink for JS8Call";
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(_waterfallRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        var back = Button("Back to radio", returnToRadio);
        back.Name = "hfLinkBack";
        var settings = Button("Settings", ConfigureAsync);
        settings.Name = "hfLinkJs8Settings";
        var openBroadcast = Button("Broadcast", () =>
        {
            _tabs.SelectedTab = _public;
            _broadcastDraft.Focus();
            return Task.CompletedTask;
        });
        openBroadcast.Name = "hfLinkOpenBroadcast";
        _installJs8 = Button("Install JS8Call…", InstallJs8Async);
        _installJs8.Visible = false;
        _waterfallToggle = Button("Hide waterfall", () =>
        {
            _showWaterfall = !_showWaterfall;
            RefreshWaterfall();
            return Task.CompletedTask;
        });
        _stopTx = Button("Stop TX", StopTransmissionAsync);
        _dial = Button("Dial…", SetDialAsync);
        var toolbar = Row(back, new Label { Text = "HFLink", AutoSize = true, Anchor = AnchorStyles.Left },
            openBroadcast, settings, _dial, _installJs8, _waterfallToggle, _activity, _stopTx);
        if (phy is not null)
        {
            var lab = Button("HF Lab", async () =>
            {
                if (_sending || _activity.Text == "TX active") throw new InvalidOperationException("Wait for transmission to finish before opening HF Lab.");
                if (_connectAllowed)
                {
                    await StopAsync();
                    _connectAllowed = false;
                    RefreshWaterfall();
                    SetActions();
                }
                SetStatus("HF Lab is ready for receive-only operation.");
                Js8PhyLab.Open(this, phy, _settings.Station?.Input);
            });
            lab.Name = "hfLinkPhyLab";
            toolbar.Controls.Add(lab);
            if (connectAllowed && string.Equals(phy, Js8PhyLab.BundledPath, StringComparison.OrdinalIgnoreCase))
                toolbar.Controls.Add(Button("Legacy JS8…", () =>
                {
                    if (_connectAllowed) return Task.CompletedTask;
                    if (MessageBox.Show(this, "Switch to the legacy JS8Call modem and its configured radio settings?", "Legacy JS8",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                        return Task.CompletedTask;
                    _connectAllowed = true;
                    EnterView();
                    return Task.CompletedTask;
                }));
        }
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_waterfall, 0, 2);
        _tabs.Name = "hfLinkJs8Tabs";
        _tabs.TabPages.AddRange([_inbox, _public, _band, _contacts, _history]);
        layout.Controls.Add(_tabs, 0, 3);

        _checkRelay = Button("Check relay", () => RelayQueryAsync(null));
        _retrieve = Button("Retrieve message", () => RelayQueryAsync(_relayMessageId));
        var reply = Button("Reply", () =>
        {
            if (_inboxRows.CurrentRow?.Tag is HfMessage message && message.Incoming) ComposeTo(message.From);
            return Task.CompletedTask;
        });
        AddGrid(_inbox, _inboxRows, Row(reply, _checkRelay, _retrieve, DeleteButton(_inboxRows)));
        AddGrid(_public, _publicRows, Row(DeleteButton(_publicRows)));
        _bandRows.Name = "hfLinkBandActivity";
        AddGrid(_band, _bandRows, Row(Button("Show earlier activity", () =>
        {
            _bandRowsToShow += BandActivityLimit;
            RefreshBandRows();
            return Task.CompletedTask;
        })));
        var contactMessage = Button("Message station", () =>
        {
            if (_contactRows.CurrentRow?.Tag is HfHeardStation station) ComposeTo(station.Callsign);
            return Task.CompletedTask;
        });
        _contactRows.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && _contactRows.Rows[args.RowIndex].Tag is HfHeardStation station) ComposeTo(station.Callsign);
        };
        var contactMap = Button("Map", () => _showContactMap?.Invoke(_store.HeardStations(false)) ?? Task.CompletedTask);
        contactMap.Enabled = _showContactMap is not null;
        AddGrid(_contacts, _contactRows, Row(contactMessage, contactMap));
        AddGrid(_history, _historyRows, Row(DeleteButton(_historyRows)));
        _send = Button("Send direct…", () => SendAsync(publicChat: false));
        _broadcast = Button("Broadcast…", () => SendAsync(publicChat: true));
        _broadcast.Name = "hfLinkSendBroadcast";
        _broadcastDraft.Name = "hfLinkBroadcastDraft";
        _draft.Name = "hfLinkDirectDraft";
        _leaveAtRelay = Button("Leave at relay…", () => SendAsync(publicChat: false, viaRelay: true));
        layout.Controls.Add(Row(_target, _destination,
            _draft, _broadcastDraft, _send, _broadcast, _leaveAtRelay), 0, 4);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _band) RefreshBandRows();
            SetActions();
        };
        SendOnEnter(_draft, _send);
        SendOnEnter(_broadcastDraft, _broadcast);
        HfLinkTheme.Apply(this);
        SetStatus(phy is not null ? "HF Lab supports receive only. Live transmission is disabled for this session." :
            _connectAllowed ? "Configure your HF station in Settings." : "JS8Call connection is disabled in this test or preview.");
        RefreshRows();
    }

    public void EnterView()
    {
        RefreshWaterfall();
        if (!_connectAllowed) return;
        string? executable = Js8CallPrerequisite.FindExecutable();
        _js8Installed = executable is not null;
        SetActions();
        if (_connection?.IsCompleted == true)
        {
            _lifetime?.Dispose();
            _lifetime = null;
            _connection = null;
        }
        if (_connection is not null) return;
        if (executable is null)
        {
            SetStatus("Install the JS8Call modem for HFLink.");
            return;
        }
        if (_settings.Station is null)
        {
            SetStatus("Open Settings to configure your HF station.");
            return;
        }
        try
        {
            _modem.Start(executable, _settings);
            _lifetime = new CancellationTokenSource();
            _connection = ConnectLoopAsync(_lifetime.Token);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or Win32Exception or System.Runtime.InteropServices.COMException)
        {
            SetStatus(error.Message);
        }
    }

    public Task LeaveViewAsync()
    {
        SaveDrafts();
        StopWaterfall();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        SaveDrafts();
        StopWaterfall();
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync();
            if (_connection is not null) await _connection;
            _lifetime.Dispose();
            _lifetime = null;
            _connection = null;
        }
        await _modem.StopAsync();
    }

    private void SaveDrafts() => _store.SaveJs8Drafts(new(_destination.Text, _draft.Text, _broadcastDraft.Text));

    private void RefreshWaterfall()
    {
        _waterfall.Visible = _showWaterfall;
        _waterfallRow.Height = _showWaterfall ? 162 : 0;
        _waterfallToggle.Text = _showWaterfall ? "Hide waterfall" : "Show waterfall";
        if (!_showWaterfall || !Visible)
        {
            StopWaterfall();
            return;
        }
        if (_connectAllowed && _settings.Station is { } station)
        {
            if (_waterfallStarted) return;
            _waterfall.Start(simulation: false, () => station.Input);
            _waterfallStarted = true;
        }
        else _waterfall.ShowStatus("Choose the radio's receive audio input in HFLink Settings.");
    }

    private void StopWaterfall()
    {
        _waterfall.Stop();
        _waterfallStarted = false;
    }

    private async Task ConnectLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var client = new Js8CallClient();
            try
            {
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(_modem.Port, connect.Token);
                }
                _client = client;
                SetStatus("Connecting to the HFLink modem…");
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var poll = PollAsync(client, session.Token);
                try
                {
                    while (true)
                    {
                        var read = client.ReadAsync(session.Token);
                        if (await Task.WhenAny(read, poll) == poll)
                        {
                            await session.CancelAsync();
                            try { await read; } catch (OperationCanceledException) { }
                            await poll;
                        }
                        HandleApiMessage(await read);
                    }
                }
                finally
                {
                    await session.CancelAsync();
                    try { await poll; } catch (OperationCanceledException) { }
                }
            }
            catch (Exception error) when (error is SocketException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                if (!stop.IsCancellationRequested)
                    SetStatus("Waiting for the HFLink modem. Check the audio devices and PTT port in Settings.");
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                SetStatus($"Could not update HFLink history. {error.Message}");
            }
            finally
            {
                _client = null;
                _callsign = "";
                _activity.Text = "";
                SetActions();
            }
            if (!_modem.IsRunning)
            {
                if (!stop.IsCancellationRequested) SetStatus("The modem stopped. Reopen HFLink to start it again.");
                return;
            }
            if (!stop.IsCancellationRequested)
                try { await Task.Delay(3000, stop); } catch (OperationCanceledException) { }
        }
    }

    private static async Task PollAsync(Js8CallClient client, CancellationToken stop)
    {
        while (true)
        {
            await client.RequestAsync("STATION.GET_CALLSIGN", "", stop);
            await client.RequestAsync("RX.GET_CALL_ACTIVITY", "", stop);
            await client.RequestAsync("RX.GET_BAND_ACTIVITY", "", stop);
            await client.RequestAsync("INBOX.GET_MESSAGES", "", stop);
            await client.RequestAsync("RIG.GET_PTT", "", stop);
            await Task.Delay(15000, stop);
        }
    }

    internal void HandleApiMessage(Js8ApiMessage data)
    {
        if (data.Type == "STATION.CALLSIGN")
        {
            if (!Js8CallClient.TryCall(data.Value, out string call) || call != _settings.Station?.Callsign)
                throw new IOException("Unexpected station on the HFLink modem port.");
            if (call.Length > 0 && call == _callsign) return;
            _callsign = call;
            _modem.KeepInBackground();
            SetStatus($"Modem connected · {_callsign}");
            _activity.Text = "RX";
            SetActions();
        }
        else if (data.Type is "RIG.PTT" or "RIG.PTT_STATUS" && _callsign.Length > 0 &&
            data.Params.ValueKind == JsonValueKind.Object && data.Params.TryGetProperty("PTT", out var ptt) &&
            ptt.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _activity.Text = ptt.GetBoolean() ? "TX active" : "RX";
            SetActions();
        }
        else if (data.Type == "RX.CALL_ACTIVITY" && data.Params.ValueKind == JsonValueKind.Object)
        {
            _store.HearJs8Stations(data.Params.EnumerateObject()
                .Where(entry => Js8CallClient.TryCall(entry.Name, out _))
                .Select(entry => Heard(entry.Name, entry.Value)));
            RefreshContacts();
        }
        else if (data.Type == "RX.SPOT" && Js8CallClient.TryCall(Js8CallClient.Text(data.Params, "CALL"), out string heard))
        {
            _store.HearJs8Stations([Heard(heard, data.Params)]);
            RefreshContacts();
        }
        else if (data.Type == "RX.ACTIVITY")
        {
            if (AddBandDecode(data.Params, data.Value) && _tabs.SelectedTab == _band) RefreshBandRows();
        }
        else if (data.Type == "RX.BAND_ACTIVITY" && data.Params.ValueKind == JsonValueKind.Object)
        {
            bool changed = false;
            foreach (var entry in data.Params.EnumerateObject())
                changed |= AddBandDecode(entry.Value, Js8CallClient.Text(entry.Value, "TEXT"));
            if (changed && _tabs.SelectedTab == _band) RefreshBandRows();
        }
        else if (data.Type == "RX.DIRECTED" && Js8CallClient.DecodeReceived(data.Params, data.Value) is { } incoming)
        {
            string frame = string.IsNullOrWhiteSpace(data.Value) ? Js8CallClient.Text(data.Params, "TEXT") : data.Value;
            if (!string.IsNullOrWhiteSpace(frame))
            {
                if (!frame.StartsWith(incoming.From + ":", StringComparison.OrdinalIgnoreCase)) frame = $"{incoming.From}: {frame}";
                if (AddBandDecode(data.Params, frame) && _tabs.SelectedTab == _band) RefreshBandRows();
            }
            _store.HearJs8Stations([Heard(incoming.From, data.Params)]);
            RefreshContacts();
            StoreReceived(incoming, notify: true);
        }
        else if (data.Type == "INBOX.MESSAGES" && data.Params.ValueKind == JsonValueKind.Object &&
            data.Params.TryGetProperty("MESSAGES", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            bool changed = false;
            foreach (var message in messages.EnumerateArray())
                if (Js8CallClient.Text(message, "type") is "READ" or "UNREAD" && message.TryGetProperty("params", out var fields) &&
                    Js8CallClient.DecodeReceived(fields) is { } stored)
                    changed |= StoreReceived(stored, notify: false);
            if (changed) RefreshRows();
        }
    }

    private bool AddBandDecode(JsonElement parameters, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || Js8CallClient.Number(parameters, "OFFSET") is not int offset) return false;
        return _store.SaveJs8BandDecode(new(Js8CallClient.Time(parameters), Js8CallClient.Number(parameters, "DIAL"),
            offset, Js8CallClient.Number(parameters, "SNR"), text));
    }

    private void RefreshBandRows()
    {
        _bandRows.Rows.Clear();
        foreach (var entry in _store.LoadJs8BandActivity(_bandRowsToShow))
            _bandRows.Rows.Add(entry.Time.ToLocalTime().ToString("HH:mm:ss"),
                entry.Dial is { } dial ? $"{dial / 1_000_000m:0.000000} MHz" : "", $"{entry.Offset} Hz",
                entry.Snr is { } snr ? $"{snr} dB" : "", entry.Text);
    }

    private HfHeardStation Heard(string call, JsonElement parameters)
    {
        call = HfStation.Call(call);
        if (Js8CallClient.Number(parameters, "SNR") is { } snr) _snr[call] = snr;
        string grid = Maidenhead.Normalize(Js8CallClient.Text(parameters, "GRID"));
        return new(call, Js8CallClient.Time(parameters), HfMessageFormat.Js8Direct, false, grid);
    }

    private bool StoreReceived(Js8Received incoming, bool notify)
    {
        if (incoming.From == _callsign || _callsign.Length == 0) return false;
        bool own = incoming.To == _callsign;
        bool publicChat = incoming.To == _settings.PublicGroup || incoming.To == "@ALLCALL";
        if (!own && !publicChat) return false;
        if (notify && own && incoming.From == _settings.Relay)
        {
            string pattern = incoming.Command == "YES" ? @"\AMSG ID ([0-9]+)(?: \+[0-9]+)?\z"
                : @"\bNEXT MSG ID ([0-9]+)(?: \+[0-9]+)?\z";
            var available = Regex.Match(incoming.Text, pattern);
            if (available.Success && int.TryParse(available.Groups[1].Value, out int id) && id > 0)
            {
                _relayMessageId = id;
                SetStatus($"Relay {incoming.From} has message {id} waiting.");
                SetActions();
            }
            else if (incoming.Command == "NO")
            {
                _relayMessageId = null;
                SetStatus($"Relay {incoming.From} reported no waiting messages.");
                SetActions();
            }
            if (incoming.Command is "YES" or "NO") return false;
        }
        if (publicChat && incoming.Command is not ("" or "MSG" or "CQ")) return false;
        if (incoming.Text.Length == 0) return false;
        if (_store.RecordJs8Received(incoming, publicChat ? HfMessageFormat.Js8Public : HfMessageFormat.Js8Direct) is not { } message) return false;
        if (notify)
        {
            RefreshRows();
            _received(message);
        }
        return true;
    }

    private static void SendOnEnter(TextBox draft, Button send)
    {
        draft.PreviewKeyDown += (_, args) => { if (args.KeyData == Keys.Enter) args.IsInputKey = true; };
        draft.KeyDown += (_, args) =>
        {
            if (args.KeyData != Keys.Enter) return;
            args.SuppressKeyPress = true;
            send.PerformClick();
        };
    }

    private async Task SendAsync(bool publicChat, bool viaRelay = false)
    {
        var draft = publicChat ? _broadcastDraft : _draft;
        string to = publicChat ? _settings.PublicGroup : HfStation.Call(_destination.Text);
        if (to == _callsign) throw new ArgumentException("Choose a different station.");
        string body = draft.Text.Trim().ToUpperInvariant();
        string command = Js8CallClient.Compose(to, body, publicChat, viaRelay ? _settings.Relay : null);
        var format = viaRelay ? HfMessageFormat.Js8Relay : publicChat ? HfMessageFormat.Js8Public : HfMessageFormat.Js8Direct;
        if (await SubmitAsync(command, to, body, format))
        {
            draft.Clear();
            SaveDrafts();
        }
    }

    private async Task RelayQueryAsync(int? id)
    {
        if (await SubmitAsync(Js8CallClient.RelayQuery(_settings.Relay, id), _settings.Relay,
            id is null ? "Check for waiting messages" : $"Retrieve message {id}", HfMessageFormat.Js8Relay) && id is not null && _relayMessageId == id)
        {
            _relayMessageId = null;
            SetActions();
        }
    }

    private async Task<bool> SubmitAsync(string command, string to, string body, HfMessageFormat format)
    {
        if (_sending || _client is not { } client || _lifetime is null || _callsign.Length == 0) return false;
        if (MessageBox.Show(this, $"Transmit as {_callsign}?\n\n{command}", "HFLink TX confirmation",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        if (!ReferenceEquals(client, _client)) throw new IOException("JS8Call disconnected during confirmation.");
        _sending = true;
        SetActions();
        try
        {
            var stop = _lifetime.Token;
            await _store.SubmitJs8Async(_callsign, to, body, format, () => client.RequestAsync("TX.SEND_MESSAGE", command, stop));
            SetStatus("Submitted to the modem for transmission.");
            return true;
        }
        finally
        {
            _sending = false;
            if (!IsDisposed) { RefreshRows(); SetActions(); }
        }
    }

    private async Task ConfigureAsync()
    {
        if (_sending) return;
        using var dialog = new HfJs8SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await StopAsync();
        _store.SaveJs8Settings(dialog.Settings);
        _settings = dialog.Settings;
        _relayMessageId = null;
        EnterView();
        SetActions();
    }

    private async Task StopTransmissionAsync()
    {
        if (_client is not { } client || _lifetime is null || _callsign.Length == 0) return;
        await client.RequestAsync("RIG.TX_HALT", "", _lifetime.Token);
        SetStatus("Stop requested on the modem.");
    }

    private async Task SetDialAsync()
    {
        if (_settings.Station is not { } station || _sending || _activity.Text == "TX active") return;
        using var dialog = new Form { Text = "HFLink dial frequency", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(405, 106), MaximizeBox = false, MinimizeBox = false };
        var frequency = HfJs8SettingsDialog.DialInput(station.DialHz);
        var save = new HfLinkTheme.ActionButton { Text = "Apply", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new HfLinkTheme.ActionButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        dialog.Controls.Add(Row(new Label { Text = "Dial frequency (MHz)", AutoSize = true, Anchor = AnchorStyles.Left }, frequency, save, cancel));
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        HfLinkTheme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK || _activity.Text == "TX active") return;
        int hz = (int)(frequency.Value * 1_000_000);
        var changed = _settings with { Station = station with { DialHz = hz } };
        _store.SaveJs8Settings(changed);
        _settings = changed;
        if (_client is { } client && _lifetime is { } lifetime && _callsign.Length > 0)
            await client.RequestAsync("RIG.SET_FREQ", "", lifetime.Token, dialHz: hz);
        SetStatus(station.CatPort.Length > 0 && _client is not null
            ? $"CAT dial change requested. Confirm {frequency.Value:0.000000} MHz on the radio."
            : $"HFLink dial saved. Set the radio to {frequency.Value:0.000000} MHz.");
        SetActions();
    }

    private async Task InstallJs8Async()
    {
        if (_installerDownload is not null) return;
        _js8Installed = Js8CallPrerequisite.IsInstalled();
        SetActions();
        if (_js8Installed || _callsign.Length > 0) return;
        if (MessageBox.Show(this, $"Download the official JS8Call {Js8CallPrerequisite.Version} installer and open Windows Setup?",
            "Install JS8Call", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        using var stop = new CancellationTokenSource();
        _installerDownload = stop;
        _installJs8.Text = "Downloading JS8Call…";
        SetActions();
        string? path = null;
        DirectoryInfo? staging = null;
        try
        {
            staging = Directory.CreateTempSubdirectory("meshcore-js8call-");
            path = Path.Combine(staging.FullName, Js8CallPrerequisite.FileName);
            await Js8CallPrerequisite.DownloadAsync(path, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (_callsign.Length > 0 || Js8CallPrerequisite.IsInstalled()) return;
            using var setup = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = staging.FullName })
                ?? throw new IOException("Windows could not open the JS8Call installer.");
            _installJs8.Text = "JS8Call setup open";
            await setup.WaitForExitAsync();
            if (IsDisposed) return;
            if (Js8CallPrerequisite.IsInstalled()) EnterView();
            else SetStatus("JS8Call setup finished. Use Install JS8Call to try again.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            UnauthorizedAccessException or Win32Exception or OperationCanceledException)
        {
            if (!IsDisposed && !stop.IsCancellationRequested)
                MessageBox.Show(this, error is OperationCanceledException ? "The JS8Call download timed out. Try again." : error.Message,
                    "JS8Call setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _installerDownload = null;
            try
            {
                if (path is not null) File.Delete(path);
                staging?.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!IsDisposed)
            {
                _js8Installed = Js8CallPrerequisite.IsInstalled();
                _installJs8.Text = "Install JS8Call…";
                SetActions();
            }
        }
    }

    public void SelectMessage(HfMessage message, bool clearFilter)
    {
        bool isPublic = message.Format == HfMessageFormat.Js8Public;
        _tabs.SelectedTab = isPublic ? _public : _inbox;
        foreach (DataGridViewRow row in (isPublic ? _publicRows : _inboxRows).Rows)
            if (row.Tag is HfMessage item && item.Id == message.Id) { row.DataGridView!.CurrentCell = row.Cells[0]; break; }
    }

    private void ComposeTo(string callsign)
    {
        _tabs.SelectedTab = _inbox;
        _destination.Text = callsign;
        _draft.Focus();
    }

    private void SetActions()
    {
        if (IsDisposed) return;
        _installJs8.Visible = _connectAllowed && !_js8Installed && _callsign.Length == 0;
        _installJs8.Enabled = _installerDownload is null;
        bool available = !_sending && _client is not null && _callsign.Length != 0;
        _stopTx.Enabled = _client is not null && _callsign.Length != 0;
        _dial.Enabled = _settings.Station is not null && _activity.Text != "TX active" && !_sending;
        _dial.Text = _settings.Station is { } station ? $"Dial {station.DialHz / 1_000_000m:0.0000} MHz…" : "Dial…";
        bool inbox = _tabs.SelectedTab == _inbox;
        bool publicChat = _tabs.SelectedTab == _public;
        bool relay = available && _settings.Relay.Length > 0 && _settings.Relay != _callsign;
        _target.Visible = inbox || publicChat;
        _target.Text = publicChat ? $"To {_settings.PublicGroup}" : "To";
        _destination.Visible = inbox;
        _draft.Visible = _send.Visible = _leaveAtRelay.Visible = inbox;
        _broadcastDraft.Visible = _broadcast.Visible = publicChat;
        _send.Enabled = available && inbox;
        _broadcast.Enabled = available && publicChat;
        _leaveAtRelay.Enabled = relay && inbox;
        _checkRelay.Enabled = relay;
        _retrieve.Enabled = relay && _relayMessageId.HasValue;
    }

    private void SetStatus(string text) { if (!IsDisposed) _status.Text = text; }

    private void RefreshRows()
    {
        var messages = _store.Messages(limit: -1);
        FillMessages(_publicRows, messages.Where(message => message.Format == HfMessageFormat.Js8Public));
        FillMessages(_inboxRows, messages.Where(message => message.Format is HfMessageFormat.Js8Direct or HfMessageFormat.Js8Relay));
        FillMessages(_historyRows, messages.Where(message => message.Format is HfMessageFormat.HfLink or HfMessageFormat.MercuryChat or HfMessageFormat.MercuryBroadcast));
        RefreshContacts();
        SetActions();
    }

    private void RefreshContacts()
    {
        string? selected = (_contactRows.CurrentRow?.Tag as HfHeardStation)?.Callsign;
        _contactRows.Rows.Clear();
        foreach (var station in _store.HeardStations(simulated: false).Where(station => station.LastFormat == HfMessageFormat.Js8Direct))
        {
            int index = _contactRows.Rows.Add(station.Callsign, station.LastHeard.ToLocalTime().ToString("g"),
                _snr.TryGetValue(station.Callsign, out int snr) ? $"{snr} dB" : "", station.Grid);
            _contactRows.Rows[index].Tag = station;
            if (selected == station.Callsign) _contactRows.CurrentCell = _contactRows.Rows[index].Cells[0];
        }
    }

    private static void FillMessages(DataGridView grid, IEnumerable<HfMessage> messages)
    {
        Guid? selected = (grid.CurrentRow?.Tag as HfMessage)?.Id;
        grid.Rows.Clear();
        foreach (var message in messages)
        {
            int index = grid.Rows.Add(message.Created.ToLocalTime().ToString("g"), message.From, message.To,
                message.Status == HfStatus.Submitted ? "Submitted to JS8Call" : message.Simulated ? $"Simulation · {message.Status}" : message.Status.ToString(), message.Text);
            grid.Rows[index].Tag = message;
            if (selected == message.Id) grid.CurrentCell = grid.Rows[index].Cells[0];
        }
    }

    private static DataGridView Grid(params string[] headings)
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, MultiSelect = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        foreach (string heading in headings)
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = heading, Width = 125, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        return grid;
    }

    private static void AddGrid(TabPage tab, DataGridView grid, Control? actions = null)
    {
        tab.Controls.Add(grid);
        if (actions is null) return;
        actions.Dock = DockStyle.Bottom;
        tab.Controls.Add(actions);
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        row.Controls.AddRange(controls);
        return row;
    }

    private Button DeleteButton(DataGridView grid) => Button("Delete message…", () =>
    {
        if (grid.CurrentRow?.Tag is HfMessage message &&
            MessageBox.Show(this, "Delete this message from HFLink on this PC?", "Delete HFLink message",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            _store.DeleteMessage(message);
            RefreshRows();
        }
        return Task.CompletedTask;
    });

    private Button Button(string text, Func<Task> action)
    {
        var button = new HfLinkTheme.ActionButton { Text = text, AutoSize = true };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or
                SocketException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException or
                System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { SetStatus(error.Message); }
        };
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _installerDownload?.Cancel();
            _lifetime?.Cancel();
        }
        base.Dispose(disposing);
    }
}
