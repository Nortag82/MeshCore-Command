using MeshCore.Windows.Core;
using System.Text;

namespace MeshCore.Windows.Preview;

internal sealed class HfLinkDialog : Form
{
    private readonly HfLink _link;
    private readonly bool _allowLive;
    private readonly Label _status = new() { AutoSize = true, Text = "Sign in to HFLink." };
    private readonly Label _station = new() { AutoSize = true };
    private readonly TextBox _destination = new() { Width = 110, MaxLength = 15, PlaceholderText = "Callsign" };
    private readonly TextBox _message = new() { Width = 420, MaxLength = 512, PlaceholderText = "Openly readable message" };
    private readonly TextBox _filter = new() { Width = 130, MaxLength = 15, PlaceholderText = "Filter by callsign" };
    private readonly ComboBox _simulation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _bytes = new() { AutoSize = true, Text = "0 / 512 bytes" };
    private readonly DataGridView _history = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _signIn, _signOut, _setup, _users, _probe, _send, _queue, _receive, _cancel, _retry, _audit, _stop;
    private Guid _session;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _finished;
    private bool _closeRequested;
    private bool _ready;

    public HfLinkDialog(string directory, bool allowLive)
    {
        _link = new HfLink(Path.Combine(directory, "hflink-native.sqlite3"));
        _allowLive = allowLive;
        Text = "MeshCore Command — Experimental HFLink";
        Size = new Size(1080, 690);
        MinimumSize = new Size(790, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(12) };
        Controls.Add(layout);
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(row switch { 6 => new RowStyle(SizeType.Percent, 100), 7 => new RowStyle(SizeType.Absolute, 90), _ => new RowStyle(SizeType.AutoSize) });
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(730, 0), ForeColor = Color.DarkRed,
            Text = "HF messages are openly readable. The designated operator is responsible for transmissions and applicable licence conditions. Callsigns and licence-check records do not verify identity, licensing or compliance."
        }, 0, 0);

        _signIn = Button("Sign in", SignIn);
        _signOut = Button("Sign out", () => { _link.SignOut(_session); _session = Guid.Empty; RefreshView(); Report("Signed out."); });
        _setup = Button("Station settings", ConfigureStation);
        _users = Button("Users and permissions", ConfigureUser);
        _audit = Button("Audit log", ShowAudit);
        layout.Controls.Add(Row(_signIn, _signOut, _setup, _users, _audit), 0, 1);
        layout.Controls.Add(_station, 0, 2);

        _queue = Button("Queue for approval", () =>
        {
            _link.Queue(_session, _destination.Text, _message.Text);
            _message.Clear();
            RefreshView();
            Report("Message is awaiting operator approval.");
        });
        layout.Controls.Add(Row(_destination, _message, _bytes, _queue), 0, 3);
        _message.TextChanged += (_, _) => _bytes.Text = $"{Encoding.UTF8.GetByteCount(_message.Text)} / 512 bytes";

        _probe = AsyncButton("Check modem", () => RunAsync(async token =>
        {
            await _link.ProbeAsync(_session, _allowLive, token);
            return _link.Station(_session).Simulation
                ? "Simulator ready. No RF is used."
                : "Mercury responded. TCP ports released after the check.";
        }));
        _receive = AsyncButton("Open receive window", ReceiveAsync);
        _stop = Button("Stop operation", () => _operation?.Cancel());
        _simulation.Items.AddRange(["Simulate no receipt", "Simulate receipt", "Simulate disconnect", "Simulate timeout"]);
        _simulation.SelectedIndex = 0;
        layout.Controls.Add(Row(_probe, _receive, _stop, _simulation), 0, 4);

        _send = AsyncButton("Approve and send", SendAsync);
        _retry = Button("Retry", () => { _link.Retry(_session, Selected().Id); RefreshView(); Report("Retry requires fresh approval."); });
        _cancel = Button("Cancel request", () => { _link.Cancel(_session, Selected().Id); RefreshView(); Report("Request cancelled."); });
        layout.Controls.Add(Row(_filter, _send, _retry, _cancel), 0, 5);
        _filter.TextChanged += (_, _) => RefreshView();
        foreach (var column in new[] { ("Time", 141), ("Mode", 85), ("Direction", 85), ("From", 95), ("To", 95), ("Status", 170) })
            _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Width = column.Item2, SortMode = DataGridViewColumnSortMode.NotSortable });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        _history.SelectionChanged += (_, _) => { if (_ready) SetActions(); };
        layout.Controls.Add(_history, 0, 6);
        layout.Controls.Add(_detail, 0, 7);
        layout.Controls.Add(_status, 0, 8);
        FormClosing += (_, e) =>
        {
            if (_operation is null) { _link.SignOut(_session); return; }
            _closeRequested = true;
            _operation.Cancel();
            e.Cancel = e.CloseReason == CloseReason.UserClosing;
        };
        _ready = true;
        RefreshView();
    }

    private void SignIn()
    {
        bool setup = _link.NeedsAdministrator;
        var name = Input("admin", 32);
        var password = Input("", 256, secret: true);
        var confirm = Input("", 256, secret: true);
        if (!Prompt(setup ? "Set the HFLink administrator password" : "Sign in to HFLink", setup
            ? [("New admin password (12+ characters)", password), ("Confirm password", confirm)]
            : [("Username", name), ("Password", password)])) return;
        if (setup && password.Text != confirm.Text) throw new ArgumentException("Passwords do not match.");
        if (_session != Guid.Empty) _link.SignOut(_session);
        _session = setup ? _link.SetupAdministrator(password.Text) : _link.SignIn(name.Text, password.Text);
        RefreshView();
        var user = _link.CurrentUser(_session);
        Report($"Signed in as {user.Name} ({user.Role})." + (setup ? " Configure the station and add a designated operator." : ""));
    }

    private void ConfigureStation()
    {
        var station = _link.Station(_session);
        var call = Input(station.Callsign, 15);
        var host = Input(station.Host, 253);
        var control = Number(station.ControlPort, 1, 65535);
        var data = Number(station.DataPort, 1, 65535);
        var timeout = Number(station.TimeoutSeconds, 5, 600);
        var sim = new CheckBox { Text = "Simulation (no RF)", Checked = station.Simulation || !_allowLive, Enabled = _allowLive, AutoSize = true };
        if (!Prompt("HFLink station settings", [("Station callsign", call), ("Mercury host", host), ("Control port", control),
            ("Data port", data), ("Session timeout in seconds", timeout), ("Mode", sim)])) return;
        _link.SaveStation(_session, new(call.Text, host.Text, (int)control.Value, (int)data.Value, (int)timeout.Value, sim.Checked));
        RefreshView();
        Report("Station settings saved. Check the modem before using it.");
    }

    private void ConfigureUser()
    {
        var users = _link.Users(_session).Where(user => user.Role != HfRole.Administrator).ToArray();
        var name = new ComboBox { Width = 255, MaxLength = 32 };
        name.Items.AddRange(users.Select(user => user.Name).Cast<object>().ToArray());
        var password = Input("", 256, secret: true);
        var call = Input("", 15);
        var role = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 255 };
        role.Items.AddRange(["User", "Designated operator"]);
        var access = new CheckBox { Text = "Approve HFLink access", AutoSize = true };
        var tx = new CheckBox { Text = "May request transmissions", AutoSize = true };
        var licence = Input("", 240);
        name.TextChanged += (_, _) =>
        {
            var existing = users.FirstOrDefault(user => user.Name == name.Text.Trim().ToLowerInvariant());
            call.Text = existing?.Callsign ?? "";
            role.SelectedIndex = existing?.Role == HfRole.Operator ? 1 : 0;
            access.Checked = existing?.Access == true;
            tx.Checked = existing?.MayRequest == true;
            licence.Text = existing?.LicenceCheck ?? "";
        };
        role.SelectedIndex = 0;
        if (!Prompt("HFLink users and permissions", [("Username (select existing or type new)", name),
            ("Password (empty to keep existing)", password), ("Callsign", call), ("Role", role),
            ("Access", access), ("Transmit requests", tx), ("Administrator’s licence check record", licence)])) return;
        _link.SaveUser(_session, new(name.Text, role.SelectedIndex == 1 ? HfRole.Operator : HfRole.User,
            call.Text, access.Checked, tx.Checked, licence.Text), password.Text);
        Report("User permissions saved.");
    }

    private async Task SendAsync()
    {
        var message = Selected();
        if (!Confirm($"Approve this transmission from {message.From} to {message.To}?\n\n{message.Text}")) return;
        await RunAsync(async token => StatusText(await _link.ApproveAndSendAsync(_session, message.Id,
            (HfSimulation)_simulation.SelectedIndex, _allowLive, token)));
    }

    private async Task ReceiveAsync()
    {
        var station = _link.Station(_session);
        if (!station.Simulation && !Confirm($"Open a receive window for {station.Callsign} for up to {station.TimeoutSeconds} seconds? Mercury may transmit ARQ control frames while listening.")) return;
        await RunAsync(async token =>
        {
            await _link.ReceiveAsync(_session, _allowLive, (message, deadline) =>
            {
                RefreshView();
                return Task.FromResult(!deadline.IsCancellationRequested && !_closeRequested &&
                    ConfirmReceipt(message, deadline));
            }, token);
            return "Receive window closed.";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _finished = finished;
        _operation = operation;
        try
        {
            Report("Modem operation in progress…");
            var pending = action(operation.Token);
            RefreshView();
            Report(await pending);
        }
        catch (OperationCanceledException) { Report(operation.IsCancellationRequested ? "Modem operation stopped." : "Mercury operation timed out."); }
        finally
        {
            _operation = null;
            _finished = null;
            try
            {
                if (!IsDisposed)
                {
                    if (_closeRequested) Close();
                    else RefreshView();
                }
            }
            finally { finished.SetResult(); }
        }
    }

    public async Task StopAndCloseAsync()
    {
        if (IsDisposed) return;
        _closeRequested = true;
        if (_finished is { } finished)
        {
            _operation?.Cancel();
            await finished.Task;
        }
        if (!IsDisposed) Close();
    }

    private bool ConfirmReceipt(HfMessage message, CancellationToken deadline)
    {
        using var dialog = new Form
        {
            Text = "HFLink receipt approval", Size = new Size(580, 270), StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        var description = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = $"Send a delivery receipt from {message.To} to {message.From}?\r\n\r\n{message.Text}"
        };
        var approve = new Button { Text = "Send receipt", DialogResult = DialogResult.Yes, AutoSize = true };
        var decline = new Button { Text = "Decline", DialogResult = DialogResult.No, AutoSize = true };
        var actions = Row(approve, decline);
        actions.Dock = DockStyle.Bottom;
        dialog.Controls.Add(description);
        dialog.Controls.Add(actions);
        dialog.CancelButton = decline;
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => { if (deadline.IsCancellationRequested) dialog.Close(); };
        timer.Start();
        return dialog.ShowDialog(this) == DialogResult.Yes && !deadline.IsCancellationRequested;
    }

    private void RefreshView()
    {
        if (IsDisposed) return;
        var selection = _history.CurrentRow?.Tag as HfMessage;
        _ready = false;
        _history.Rows.Clear();
        _station.Text = "Sign in to view the station and conversation history.";
        if (_session != Guid.Empty)
        {
            try
            {
                var station = _link.Station(_session);
                _station.Text = $"Station: {(station.Callsign.Length == 0 ? "not configured" : station.Callsign)}   Mode: {(station.Simulation ? "simulation" : "Mercury")}";
                var filter = _filter.Text.Trim().ToUpperInvariant();
                foreach (var message in _link.Messages(_session).Where(m => filter.Length == 0 || m.From.Contains(filter) || m.To.Contains(filter)))
                {
                    int index = _history.Rows.Add(message.Created.ToLocalTime().ToString("g"), message.Simulated ? "Simulation" : "Mercury",
                        message.Incoming ? "Incoming" : "Outgoing", message.From, message.To, StatusText(message.Status), message.Text);
                    _history.Rows[index].Tag = message;
                    if (selection?.Id == message.Id && selection.From == message.From) _history.CurrentCell = _history.Rows[index].Cells[0];
                }
            }
            catch (UnauthorizedAccessException error) { _station.Text = error.Message; }
        }
        _ready = true;
        SetActions();
    }

    private void SetActions()
    {
        HfUser? user = null;
        if (_session != Guid.Empty)
            try { user = _link.CurrentUser(_session); }
            catch (UnauthorizedAccessException) { }
        bool idle = _operation is null;
        bool admin = user?.Role == HfRole.Administrator;
        bool op = user is { Role: HfRole.Operator, Access: true };
        bool access = admin || user?.Access == true;
        var message = _history.CurrentRow?.Tag as HfMessage;
        _signIn.Enabled = idle && user is null;
        _signOut.Enabled = idle && _session != Guid.Empty;
        _setup.Enabled = _users.Enabled = idle && admin;
        _audit.Enabled = idle && (admin || op);
        _queue.Enabled = idle && user is { Access: true, MayRequest: true };
        _receive.Enabled = idle && op;
        _probe.Enabled = idle && (admin || op);
        _stop.Enabled = !idle;
        _simulation.Enabled = idle && op && _link.Station(_session).Simulation;
        _send.Enabled = idle && op && message is { Incoming: false, Status: HfStatus.AwaitingApproval };
        _cancel.Enabled = idle && access && message is { Incoming: false, Status: HfStatus.AwaitingApproval };
        _retry.Enabled = idle && access && message is { Incoming: false, Status: HfStatus.Failed or HfStatus.Unconfirmed };
        _detail.Text = message is null ? "" : message.Text + Environment.NewLine + message.Detail +
            (message.Incoming ? "" : $"\r\nRequested by {message.Requester} ({message.RequesterCall})\r\nID: {message.Id}");
    }

    private void ShowAudit()
    {
        using var audit = new Form { Text = "HFLink audit", Size = new Size(860, 480), StartPosition = FormStartPosition.CenterParent };
        audit.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = string.Join(Environment.NewLine, _link.Audit(_session).Select(item =>
                $"{item.Time.ToLocalTime():g}    {item.Actor}    {item.Event}    {item.Message}"))
        });
        audit.ShowDialog(this);
    }

    private HfMessage Selected() => _history.CurrentRow?.Tag as HfMessage ?? throw new InvalidOperationException("Select a message first.");
    private void Report(string text) { if (!IsDisposed) _status.Text = text; }
    private bool Confirm(string text) => MessageBox.Show(this, text, "HFLink operator approval",
        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    private static string StatusText(HfStatus status) => status switch
    {
        HfStatus.AwaitingApproval => "Awaiting operator approval",
        HfStatus.Sent => "Sent to local modem",
        HfStatus.Delivered => "Delivered to destination station",
        _ => status.ToString()
    };

    private Button Button(string title, Action action) => AsyncButton(title, () => { action(); return Task.CompletedTask; });
    private Button AsyncButton(string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException or
                IOException or InvalidDataException or System.Net.Sockets.SocketException or Microsoft.Data.Sqlite.SqliteException)
            { Report(error is Microsoft.Data.Sqlite.SqliteException ? "HFLink storage operation failed." : error.Message); }
        };
        return button;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        row.Controls.AddRange(controls);
        return row;
    }

    private static TextBox Input(string text, int max, bool secret = false) =>
        new() { Text = text, Width = 255, MaxLength = max, UseSystemPasswordChar = secret };

    private static NumericUpDown Number(int value, int min, int max) =>
        new() { Minimum = min, Maximum = max, Value = value, Width = 110 };

    private bool Prompt(string title, (string Label, Control Control)[] fields)
    {
        using var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
        };
        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        for (int i = 0; i < fields.Length; i++)
        {
            form.Controls.Add(new Label { Text = fields[i].Label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            form.Controls.Add(fields[i].Control, 1, i);
        }
        var save = new Button { Text = "Save / continue", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        form.Controls.Add(Row(save, cancel), 1, fields.Length);
        dialog.Controls.Add(form);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        bool accepted = dialog.ShowDialog(this) == DialogResult.OK;
        foreach (var field in fields) form.Controls.Remove(field.Control);
        return accepted;
    }
}
