using MeshCore.Windows.Core;
using System.Text;

namespace MeshCore.Windows.Preview;

internal sealed class HfLinkView : UserControl
{
    private readonly HfLink _link;
    private readonly bool _allowLive;
    private readonly BundledMercury _bundled;
    private readonly Label _status = new() { AutoSize = true, Text = "Ready." };
    private readonly Label _station = new() { AutoSize = true };
    private readonly TextBox _destination = new() { Width = 110, MaxLength = 15, AccessibleName = "Destination callsign" };
    private readonly TextBox _message = new() { Width = 360, MaxLength = 512, AccessibleName = "Message" };
    private readonly TextBox _filter = new() { Width = 130, MaxLength = 15, AccessibleName = "Filter by callsign" };
    private readonly ComboBox _simulation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _bytes = new() { AutoSize = true, Text = "0 / 512 bytes" };
    private readonly DataGridView _history = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _settings, _probe, _send, _queue, _receive, _cancel, _retry, _audit, _stop, _stopMercury;
    private readonly Action<HfMessage> _received;
    private readonly FlowLayoutPanel _receipt;
    private readonly Label _receiptLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _finished;
    private TaskCompletionSource<bool>? _receiptDecision;
    private bool _receiving, _away, _checkingFirewall;
    private bool _ready;

    public HfLinkView(string directory, bool allowLive, Func<Task> returnToRadio, Action<HfMessage> received)
    {
        _link = new HfLink(Path.Combine(directory, "hflink-native.sqlite3"));
        _allowLive = allowLive;
        _received = received;
        _bundled = new BundledMercury(directory);
        _bundled.ModemStopped += detail =>
        {
            _operation?.Cancel();
            Report(detail);
        };
        Dock = DockStyle.Fill;
        AccessibleName = "HFLink";
        Font = new Font("Segoe UI", 11);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new Padding(16), AutoScroll = true };
        Controls.Add(layout);
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(row switch { 7 => new RowStyle(SizeType.Percent, 100), 8 => new RowStyle(SizeType.Absolute, 90), _ => new RowStyle(SizeType.AutoSize) });
        var back = AsyncButton("Back to radio", returnToRadio);
        back.Name = "hfLinkBack";
        var title = new Label { Text = "HFLink", Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(8, 0, 0, 0) };
        layout.Controls.Add(Row(back, title), 0, 0);
        var notice = new Label
        {
            AutoSize = true, MaximumSize = new Size(730, 0), ForeColor = SystemColors.WindowText,
            Text = "HF messages are openly readable. The designated operator is responsible for transmissions and applicable licence conditions. Use your station callsign and check that the frequency is clear before sending."
        };
        layout.Controls.Add(notice, 0, 1);

        _settings = Button("Settings", ConfigureSettings);
        _settings.Name = "hfLinkSettings";
        _audit = Button("Audit log", ShowAudit);
        layout.Controls.Add(Row(_settings, _audit), 0, 2);
        layout.Controls.Add(_station, 0, 3);

        _queue = Button("Queue for approval", () =>
        {
            _link.Queue(_destination.Text, _message.Text);
            _message.Clear();
            RefreshView();
            Report("Message is awaiting operator approval.");
        });
        layout.Controls.Add(Row(FieldLabel(_destination), _destination, FieldLabel(_message), _message, _bytes, _queue), 0, 4);
        _message.TextChanged += (_, _) => _bytes.Text = $"{Encoding.UTF8.GetByteCount(_message.Text)} / 512 bytes";

        _probe = AsyncButton("Check modem", () => RunAsync(async token =>
        {
            await PrepareModemAsync(transmission: false, token);
            await _link.ProbeAsync(_allowLive, token);
            return _link.Station().Simulation
                ? "Simulator ready. No RF is used."
                : "Mercury responded. TCP ports released after the check.";
        }));
        _receive = AsyncButton("Open receive window", ReceiveAsync);
        _receive.Name = "hfLinkReceive";
        _stop = Button("Stop operation", () => _operation?.Cancel());
        _stop.Name = "hfLinkStopOperation";
        _stopMercury = Button("Stop Mercury", () => { _bundled.Stop(); RefreshView(); Report("Bundled Mercury stopped."); });
        _simulation.Items.AddRange(["Simulate no receipt", "Simulate receipt", "Simulate disconnect", "Simulate timeout"]);
        _simulation.SelectedIndex = 0;
        layout.Controls.Add(Row(_probe, _receive, _stop, _stopMercury, _simulation), 0, 5);

        _send = AsyncButton("Approve and send", SendAsync);
        _retry = Button("Retry", () => { _link.Retry(Selected().Id); RefreshView(); Report("Retry requires fresh approval."); });
        _cancel = Button("Cancel request", () => { _link.Cancel(Selected().Id); RefreshView(); Report("Request cancelled."); });
        layout.Controls.Add(Row(FieldLabel(_filter), _filter, _send, _retry, _cancel), 0, 6);
        _filter.TextChanged += (_, _) => RefreshView();
        foreach (var column in new[] { ("Time", 141), ("Mode", 85), ("Direction", 85), ("From", 95), ("To", 95), ("Status", 170) })
            _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Width = column.Item2, SortMode = DataGridViewColumnSortMode.NotSortable });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        _history.CurrentCellChanged += (_, _) => { if (_ready) SetActions(); };
        layout.Controls.Add(_history, 0, 7);
        layout.Controls.Add(_detail, 0, 8);
        _receipt = Row(_receiptLabel, Button("Send receipt", () => _receiptDecision?.TrySetResult(true)),
            Button("Decline receipt", () => _receiptDecision?.TrySetResult(false)));
        _receipt.Visible = false;
        layout.Controls.Add(_receipt, 0, 9);
        layout.Controls.Add(_status, 0, 10);
        if (!SystemInformation.HighContrast)
        {
            StyleView(layout);
            notice.ForeColor = Color.FromArgb(255, 204, 132);
        }
        _ready = true;
        RefreshView();
    }

    private static void StyleView(Control container)
    {
        Color background = Color.FromArgb(9, 15, 20);
        Color surface = Color.FromArgb(21, 37, 48);
        Color foreground = Color.FromArgb(234, 242, 247);
        container.BackColor = background;
        container.ForeColor = foreground;
        foreach (Control child in container.Controls)
        {
            child.ForeColor = foreground;
            switch (child)
            {
                case Panel:
                    StyleView(child);
                    break;
                case Button button:
                    button.UseVisualStyleBackColor = false;
                    button.BackColor = surface;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = Color.FromArgb(56, 80, 94);
                    break;
                case DataGridView grid:
                    grid.BackgroundColor = background;
                    grid.GridColor = Color.FromArgb(57, 81, 95);
                    grid.EnableHeadersVisualStyles = false;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = surface;
                    grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                    grid.DefaultCellStyle.BackColor = background;
                    grid.DefaultCellStyle.ForeColor = foreground;
                    grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(30, 92, 87);
                    grid.DefaultCellStyle.SelectionForeColor = Color.White;
                    break;
                case TextBoxBase:
                    child.BackColor = surface;
                    break;
                case ComboBox:
                    child.ForeColor = SystemColors.WindowText;
                    child.BackColor = SystemColors.Window;
                    break;
            }
        }
    }

    private void ConfigureSettings()
    {
        using var dialog = CreateSettingsDialog();
        dialog.ShowDialog(this);
        EnterView();
    }

    internal Form CreateSettingsDialog()
    {
        var station = _link.Station();
        var dialog = new Form
        {
            Text = "HFLink Settings", StartPosition = FormStartPosition.CenterParent, MinimumSize = new Size(660, 520),
            Size = new Size(740, 580), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var stationTab = new TabPage("Station");
        var modemTab = new TabPage("Audio and PTT");
        var firewallTab = new TabPage("Firewall");
        tabs.TabPages.AddRange([stationTab, modemTab, firewallTab]);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var footer = Row(close);
        footer.Dock = DockStyle.Bottom;
        dialog.Controls.Add(tabs);
        dialog.Controls.Add(footer);
        dialog.CancelButton = close;

        var call = Input(station.Callsign, 15);
        var host = Input(station.Host, 253);
        var control = Number(station.ControlPort, 1, 65535);
        var data = Number(station.DataPort, 1, 65535);
        var timeout = Number(station.TimeoutSeconds, 5, 600);
        var bundled = new CheckBox { Text = "Use bundled Mercury", Checked = station.UseBundledMercury, AutoSize = true };
        void SetExternalFields() => host.Enabled = control.Enabled = data.Enabled = !bundled.Checked;
        bundled.CheckedChanged += (_, _) => SetExternalFields();
        SetExternalFields();
        var sim = new CheckBox { Text = "Simulation (no RF)", Checked = station.Simulation || !_allowLive, Enabled = _allowLive, AutoSize = true };
        var stationStatus = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };
        var save = new Button { Text = "Save station", AutoSize = true };
        var form = Fields([("Station callsign", call), ("Modem", bundled), ("External Mercury host", host), ("Control port", control),
            ("Data port", data), ("Session timeout in seconds", timeout), ("Mode", sim), ("", save), ("", stationStatus)]);
        stationTab.Controls.Add(form);
        bool? modemMode = null;
        void LoadModem()
        {
            bool managed = station.UseBundledMercury;
            if (modemMode == managed) return;
            string? path = managed ? _bundled.PrepareSettings() : _link.MercuryConfigPath() ?? MercurySettingsPanel.FindConfig();
            if (modemTab.Controls.Count != 0) modemTab.Controls[0].Dispose();
            modemTab.Controls.Add(new MercurySettingsPanel(path, config =>
            {
                string? backup = _link.SaveMercuryConfig(config, rememberPath: !managed);
                if (managed) _bundled.Stop();
                RefreshView();
                return backup;
            }, fixedPath: managed));
            modemMode = managed;
        }
        tabs.Selected += (_, _) =>
        {
            if (tabs.SelectedTab != modemTab) return;
            try { LoadModem(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                MessageBox.Show(dialog, error.Message, "HFLink Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        save.Click += (_, _) =>
        {
            try
            {
                bool simulation = _allowLive ? sim.Checked : station.Simulation;
                _link.SaveStation(new(call.Text, host.Text, (int)control.Value, (int)data.Value, (int)timeout.Value, simulation, bundled.Checked));
                station = _link.Station();
                _bundled.Stop();
                RefreshView();
                stationStatus.Text = "Station settings saved.";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { stationStatus.Text = error.Message; }
        };

        var firewallStatus = new Label { AutoSize = true, MaximumSize = new Size(565, 0) };
        var fix = new Button { Text = "Fix firewall", AutoSize = true, Enabled = _allowLive && BundledMercury.Available };
        var description = new Label
        {
            AutoSize = true, MaximumSize = new Size(565, 0),
            Text = "Bundled Mercury requires Windows Firewall to be enabled on all network profiles. Setup blocks remote TCP access to the modem and requires administrator approval."
        };
        firewallTab.Controls.Add(Fields([("", description), ("", fix), ("", firewallStatus)]));
        fix.Click += async (_, _) =>
        {
            if (!ConfirmFirewall(dialog)) return;
            fix.Enabled = close.Enabled = false;
            dialog.ControlBox = false;
            firewallStatus.Text = "Checking Windows Firewall…";
            try { firewallStatus.Text = await FixFirewallAsync(CancellationToken.None); }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { firewallStatus.Text = error.Message; }
            finally
            {
                if (!dialog.IsDisposed) { dialog.ControlBox = true; fix.Enabled = close.Enabled = true; }
            }
        };
        return dialog;
    }

    public void EnterView()
    {
        _away = false;
        if (!_allowLive || _checkingFirewall || _operation is not null || !BundledMercury.Available) return;
        BeginInvoke(async () => await OfferFirewallSetupAsync());
    }

    private async Task OfferFirewallSetupAsync()
    {
        if (_checkingFirewall || _away || IsDisposed) return;
        _checkingFirewall = true;
        try
        {
            if (_link.FirewallSetupOffered() || !_link.Station().UseBundledMercury) return;
            bool protectedAlready = await _bundled.IsFirewallProtectedAsync(CancellationToken.None);
            if (_away || IsDisposed || _operation is not null || Form.ActiveForm != FindForm()) return;
            _link.MarkFirewallSetupOffered();
            if (!protectedAlready && ConfirmFirewall(this)) await RunAsync(FixFirewallAsync);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Report(error.Message); }
        finally { _checkingFirewall = false; }
    }

    private static bool ConfirmFirewall(IWin32Window owner) =>
        MessageBox.Show(owner, "Enable Windows Firewall on all network profiles and block remote access to bundled Mercury? Windows will ask for administrator approval. You can also fix this later in HFLink Settings.",
            "Set up HFLink firewall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private async Task<string> FixFirewallAsync(CancellationToken stop)
    {
        await _bundled.ConfigureFirewallAsync(stop);
        return "Firewall protection verified.";
    }

    private Task PrepareModemAsync(bool transmission, CancellationToken token)
    {
        var station = _link.Station();
        return !station.Simulation && station.UseBundledMercury && _allowLive
            ? _bundled.EnsureReadyAsync(transmission, token) : Task.CompletedTask;
    }

    private async Task SendAsync()
    {
        var message = Selected();
        if (!Confirm($"Approve this transmission from {message.From} to {message.To}?\n\n{message.Text}")) return;
        await RunAsync(async token =>
        {
            await PrepareModemAsync(transmission: true, token);
            return StatusText(await _link.ApproveAndSendAsync(message.Id, (HfSimulation)_simulation.SelectedIndex, _allowLive, token));
        });
    }

    private async Task ReceiveAsync()
    {
        var station = _link.Station();
        if (!station.Simulation && !Confirm($"Open a receive window for {station.Callsign} for up to {station.TimeoutSeconds} seconds? Mercury may transmit ARQ control frames while listening.")) return;
        await RunAsync(async token =>
        {
            await PrepareModemAsync(transmission: true, token);
            Report("Receive window open. You can return to the radio page while listening.");
            await _link.ReceiveAsync(_allowLive, ApproveReceiptAsync, token, _received);
            return "Receive window closed.";
        }, receiving: true);
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> action, bool receiving = false)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _finished = finished;
        _operation = operation;
        _receiving = receiving;
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
            _receiving = false;
            try
            {
                if (receiving && _away) _bundled.Stop();
                if (!IsDisposed) RefreshView();
            }
            finally { finished.SetResult(); }
        }
    }

    public Task LeaveViewAsync()
    {
        _away = true;
        return _receiving ? Task.CompletedTask : StopAsync();
    }

    public async Task StopAsync()
    {
        if (_finished is { } finished)
        {
            _operation?.Cancel();
            await finished.Task;
        }
        _bundled.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operation?.Cancel();
            _bundled.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task<bool> ApproveReceiptAsync(HfMessage message, CancellationToken deadline)
    {
        if (deadline.IsCancellationRequested) return false;
        RefreshView();
        SelectMessage(message);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiptDecision = decision;
        _receiptLabel.Text = $"Send delivery receipt to {message.From}?";
        _receipt.Visible = true;
        try { return await decision.Task.WaitAsync(deadline) && !deadline.IsCancellationRequested; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
        finally
        {
            _receiptDecision = null;
            if (!IsDisposed) _receipt.Visible = false;
        }
    }

    public void SelectMessage(HfMessage message, bool clearFilter = false)
    {
        string filter = _filter.Text.Trim();
        if (clearFilter && !message.From.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
            !message.To.Contains(filter, StringComparison.OrdinalIgnoreCase))
            _filter.Clear();
        foreach (DataGridViewRow row in _history.Rows)
        {
            if (row.Tag is HfMessage stored && stored.Id == message.Id && stored.From == message.From &&
                stored.To == message.To && stored.Simulated == message.Simulated)
            {
                _history.CurrentCell = row.Cells[0];
                break;
            }
        }
    }

    private void RefreshView()
    {
        if (IsDisposed) return;
        var selection = _history.CurrentRow?.Tag as HfMessage;
        _ready = false;
        _history.Rows.Clear();
        var station = _link.Station();
        string mode = station.Simulation ? "simulation" : station.UseBundledMercury ? "bundled Mercury" : "external Mercury";
        _station.Text = $"Station: {(station.Callsign.Length == 0 ? "not configured" : station.Callsign)}   Mode: {mode}";
        var filter = _filter.Text.Trim().ToUpperInvariant();
        foreach (var message in _link.Messages().Where(m => filter.Length == 0 || m.From.Contains(filter) || m.To.Contains(filter)))
        {
            int index = _history.Rows.Add(message.Created.ToLocalTime().ToString("g"), message.Simulated ? "Simulation" : "Mercury",
                message.Incoming ? "Incoming" : "Outgoing", message.From, message.To, StatusText(message.Status), message.Text);
            _history.Rows[index].Tag = message;
            if (selection?.Id == message.Id && selection.From == message.From) _history.CurrentCell = _history.Rows[index].Cells[0];
        }
        _ready = true;
        SetActions();
    }

    private void SetActions()
    {
        var station = _link.Station();
        bool idle = _operation is null;
        bool configured = station.Callsign.Length != 0;
        bool liveAllowed = station.Simulation || _allowLive;
        var message = _history.CurrentRow?.Tag as HfMessage;
        _settings.Enabled = _audit.Enabled = idle;
        _stopMercury.Visible = station.UseBundledMercury && !station.Simulation;
        _stopMercury.Enabled = idle && _bundled.Running;
        _queue.Enabled = idle && configured;
        _receive.Enabled = _probe.Enabled = idle && configured && liveAllowed;
        _stop.Enabled = !idle;
        _simulation.Enabled = idle && station.Simulation;
        _send.Enabled = idle && configured && liveAllowed && message is { Incoming: false, Status: HfStatus.AwaitingApproval };
        _cancel.Enabled = idle && message is { Incoming: false, Status: HfStatus.AwaitingApproval };
        _retry.Enabled = idle && message is { Incoming: false, Status: HfStatus.Failed or HfStatus.Unconfirmed };
        _detail.Text = message is null ? "" : message.Text + Environment.NewLine + message.Detail +
            (message.Incoming ? "" : $"\r\nRequested by {message.Requester} ({message.RequesterCall})\r\nID: {message.Id}");
    }

    private void ShowAudit()
    {
        using var audit = new Form { Text = "HFLink audit", Size = new Size(860, 480), StartPosition = FormStartPosition.CenterParent };
        audit.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = string.Join(Environment.NewLine, _link.Audit().Select(item =>
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
        var button = new HfLinkButton { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or InvalidOperationException or
                IOException or InvalidDataException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or Microsoft.Data.Sqlite.SqliteException)
            { Report(error is Microsoft.Data.Sqlite.SqliteException ? "HFLink storage operation failed." : error.Message); }
        };
        return button;
    }

    private sealed class HfLinkButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled || FlatStyle != FlatStyle.Flat || SystemInformation.HighContrast)
            {
                base.OnPaint(e);
                return;
            }

            e.Graphics.Clear(BackColor);
            using var border = new Pen(FlatAppearance.BorderColor);
            e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(164, 183, 195),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private static Label FieldLabel(Control field) => new()
    {
        Text = field.AccessibleName,
        AutoSize = true,
        Anchor = AnchorStyles.Left
    };

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

    private static TableLayoutPanel Fields((string Label, Control Control)[] fields)
    {
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(12) };
        for (int i = 0; i < fields.Length; i++)
        {
            var (label, control) = fields[i];
            if (label.Length == 0)
            {
                form.Controls.Add(control, 0, i);
                form.SetColumnSpan(control, 2);
            }
            else
            {
                form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
                form.Controls.Add(control, 1, i);
            }
        }
        return form;
    }
}
