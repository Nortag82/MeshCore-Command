using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal sealed class HfJs8View : UserControl
{
    private readonly HfLink _store;
    private readonly bool _connectAllowed;
    private readonly Action<HfMessage> _received;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _inbox = new("Inbox"), _public = new("Public"), _contacts = new("Contacts"), _history = new("Earlier HFLink history");
    private readonly DataGridView _inboxRows = Grid("Time", "From", "To", "Status", "Message");
    private readonly DataGridView _publicRows = Grid("Time", "From", "To", "Status", "Message");
    private readonly DataGridView _contactRows = Grid("Callsign", "Last heard", "SNR");
    private readonly DataGridView _historyRows = Grid("Time", "From", "To", "Status", "Message");
    private readonly TextBox _destination = new() { Width = 120, MaxLength = 15, AccessibleName = "Destination callsign" };
    private readonly TextBox _draft = new() { Width = 400, MaxLength = 180, AccessibleName = "JS8 message" };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _target = new() { Text = "To", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _send, _leaveAtRelay, _checkRelay, _retrieve;
    private readonly Dictionary<string, int> _snr = new(StringComparer.Ordinal);
    private Js8LinkSettings _settings;
    private CancellationTokenSource? _lifetime;
    private Task? _connection;
    private Js8CallClient? _client;
    private string _callsign = "";
    private bool _sending;
    private int? _relayMessageId;

    public HfJs8View(string directory, bool connectAllowed, Func<Task> returnToRadio, Action<HfMessage> received)
    {
        _store = new HfLink(Path.Combine(directory, "hflink-native.sqlite3"), stationSimulation: false);
        _settings = _store.Js8Settings();
        _connectAllowed = connectAllowed;
        _received = received;
        Dock = DockStyle.Fill;
        AccessibleName = "HFLink for JS8Call";
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        var back = Button("Back to radio", returnToRadio);
        back.Name = "hfLinkBack";
        var settings = Button("Settings", ConfigureAsync);
        settings.Name = "hfLinkJs8Settings";
        layout.Controls.Add(Row(back, new Label { Text = "HFLink · JS8Call", AutoSize = true, Anchor = AnchorStyles.Left }, settings), 0, 0);
        layout.Controls.Add(_status, 0, 1);
        _tabs.Name = "hfLinkJs8Tabs";
        _tabs.TabPages.AddRange([_inbox, _public, _contacts, _history]);
        layout.Controls.Add(_tabs, 0, 2);

        _checkRelay = Button("Check relay", () => RelayQueryAsync(null));
        _retrieve = Button("Retrieve message", () => RelayQueryAsync(_relayMessageId));
        var reply = Button("Reply", () =>
        {
            if (_inboxRows.CurrentRow?.Tag is HfMessage message && message.Incoming) ComposeTo(message.From);
            return Task.CompletedTask;
        });
        AddGrid(_inbox, _inboxRows, Row(reply, _checkRelay, _retrieve, DeleteButton(_inboxRows)));
        AddGrid(_public, _publicRows, Row(DeleteButton(_publicRows)));
        var contactMessage = Button("Message station", () =>
        {
            if (_contactRows.CurrentRow?.Tag is HfHeardStation station) ComposeTo(station.Callsign);
            return Task.CompletedTask;
        });
        _contactRows.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0 && _contactRows.Rows[args.RowIndex].Tag is HfHeardStation station) ComposeTo(station.Callsign);
        };
        AddGrid(_contacts, _contactRows, Row(contactMessage));
        AddGrid(_history, _historyRows, Row(DeleteButton(_historyRows)));
        _send = Button("Send…", () => SendAsync(viaRelay: false));
        _leaveAtRelay = Button("Leave at relay…", () => SendAsync(viaRelay: true));
        layout.Controls.Add(Row(_target, _destination,
            _draft, _send, _leaveAtRelay), 0, 3);
        _tabs.SelectedIndexChanged += (_, _) => SetActions();
        _draft.PreviewKeyDown += (_, args) => { if (args.KeyData == Keys.Enter) args.IsInputKey = true; };
        _draft.KeyDown += (_, args) =>
        {
            if (args.KeyData != Keys.Enter) return;
            args.SuppressKeyPress = true;
            _send.PerformClick();
        };
        SetStatus(_connectAllowed ? "Open JS8Call and enable its local TCP Network Message API." : "JS8Call connection is disabled in this test or preview.");
        RefreshRows();
    }

    public void EnterView()
    {
        if (_connectAllowed && _connection is null)
        {
            _lifetime = new CancellationTokenSource();
            _connection = ConnectLoopAsync(_lifetime.Token);
        }
    }

    public Task LeaveViewAsync() => Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_lifetime is null) return;
        await _lifetime.CancelAsync();
        if (_connection is not null) await _connection;
        _lifetime.Dispose();
        _lifetime = null;
        _connection = null;
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
                    await client.ConnectAsync(_settings.Port, connect.Token);
                }
                _client = client;
                SetStatus("Waiting for JS8Call station details. In JS8Call Settings → Reporting, enable Accept TCP Requests.");
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
                    SetStatus($"Waiting for JS8Call on TCP port {_settings.Port}. Check Settings → Reporting in JS8Call.");
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                SetStatus($"Could not update HFLink history. {error.Message}");
            }
            finally
            {
                _client = null;
                _callsign = "";
                SetActions();
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
            await client.RequestAsync("INBOX.GET_MESSAGES", "", stop);
            await Task.Delay(15000, stop);
        }
    }

    private void HandleApiMessage(Js8ApiMessage data)
    {
        if (data.Type == "STATION.CALLSIGN")
        {
            Js8CallClient.TryCall(data.Value, out string call);
            if (call.Length > 0 && call == _callsign) return;
            _callsign = call;
            SetStatus(_callsign.Length > 0 ? $"Connected to JS8Call · {_callsign}" : "Set your station callsign in JS8Call.");
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
        else if (data.Type == "RX.DIRECTED" && Js8CallClient.DecodeReceived(data.Params, data.Value) is { } incoming)
        {
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

    private HfHeardStation Heard(string call, JsonElement parameters)
    {
        call = HfStation.Call(call);
        if (Js8CallClient.Number(parameters, "SNR") is { } snr) _snr[call] = snr;
        return new(call, Js8CallClient.Time(parameters), HfMessageFormat.Js8Direct, false);
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

    private async Task SendAsync(bool viaRelay)
    {
        bool publicChat = _tabs.SelectedTab == _public;
        string to = publicChat ? _settings.PublicGroup : HfStation.Call(_destination.Text);
        if (to == _callsign) throw new ArgumentException("Choose a different station.");
        string body = _draft.Text.Trim().ToUpperInvariant();
        string command = Js8CallClient.Compose(to, body, publicChat, viaRelay ? _settings.Relay : null);
        var format = viaRelay ? HfMessageFormat.Js8Relay : publicChat ? HfMessageFormat.Js8Public : HfMessageFormat.Js8Direct;
        if (await SubmitAsync(command, to, body, format)) _draft.Clear();
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
            SetStatus("Submitted to JS8Call. Check JS8Call for transmit progress.");
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
        using var dialog = new Form { Text = "HFLink JS8Call settings", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, 170), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _settings.Port };
        var group = new TextBox { Text = _settings.PublicGroup, MaxLength = 9 };
        var relay = new TextBox { Text = _settings.Relay, MaxLength = 15 };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        foreach (var (name, field) in new (string, Control)[] { ("Local JS8Call TCP port", port), ("Public JS8Call group", group), ("Inbox relay callsign", relay) })
        {
            form.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left });
            form.Controls.Add(field);
        }
        var actions = Row(save, cancel);
        form.Controls.Add(actions);
        form.SetColumnSpan(actions, 2);
        dialog.Controls.Add(form);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var settings = new Js8LinkSettings((int)port.Value, group.Text, relay.Text).Validate();
        _store.SaveJs8Settings(settings);
        _settings = settings;
        _relayMessageId = null;
        await StopAsync();
        EnterView();
        SetActions();
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
        bool available = !_sending && _client is not null && _callsign.Length != 0;
        bool inbox = _tabs.SelectedTab == _inbox;
        bool publicChat = _tabs.SelectedTab == _public;
        bool relay = available && _settings.Relay.Length > 0 && _settings.Relay != _callsign;
        _target.Visible = inbox || publicChat;
        _target.Text = publicChat ? $"To {_settings.PublicGroup}" : "To";
        _destination.Visible = inbox;
        _draft.Enabled = inbox || publicChat;
        _send.Enabled = available && _draft.Enabled;
        _leaveAtRelay.Enabled = relay && inbox;
        _checkRelay.Enabled = relay;
        _retrieve.Enabled = relay && _relayMessageId.HasValue;
    }

    private void SetStatus(string text) { if (!IsDisposed) _status.Text = text; }

    private void RefreshRows()
    {
        var messages = _store.Messages();
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
                _snr.TryGetValue(station.Callsign, out int snr) ? $"{snr} dB" : "");
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
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or
                SocketException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException) { SetStatus(error.Message); }
        };
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _lifetime?.Cancel();
        base.Dispose(disposing);
    }
}
