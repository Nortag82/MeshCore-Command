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
    private readonly TextBox _destination = new() { Name = "hfLinkDestination", Width = 110, MaxLength = 15, AccessibleName = "Destination callsign" };
    private readonly TextBox _message = new() { Name = "hfLinkDraft", Width = 360, MaxLength = HfPacket.TextLimit, AccessibleName = "Message" };
    private readonly TextBox _filter = new() { Width = 130, MaxLength = 15, AccessibleName = "Filter by callsign" };
    private readonly ComboBox _format = new() { Name = "hfLinkMessageFormat", DropDownStyle = ComboBoxStyle.DropDownList, Width = 176, AccessibleName = "Format" };
    private readonly ComboBox _simulation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _bytes = new() { AutoSize = true, Text = "0 / 512 bytes" };
    private readonly DataGridView _history = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly HfWaterfallView _waterfall = new() { Visible = false };
    private readonly HfSessionStatus _session = new();
    private readonly RowStyle _waterfallRow = new(SizeType.Absolute, 0);
    private readonly Button _waterfallToggle;
    private readonly Button _settings, _probe, _send, _sendDraft, _receive, _receiveBroadcast, _cancel, _retry, _reply, _delete, _audit, _stop, _stopMercury;
    private readonly Action<HfMessage> _received;
    private readonly Func<string, bool> _confirmTransmission;
    private readonly FlowLayoutPanel _receipt;
    private readonly Label _receiptLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _finished;
    private TaskCompletionSource<bool>? _receiptDecision;
    private HfHeardStationsWindow? _heardStationsWindow;
    private bool _receiving, _checkingFirewall, _checkingMercuryUpdate;
    private bool _away = true;
    private Version? _offeredMercuryUpdate;
    private readonly CancellationTokenSource _viewLifetime = new();
    private bool _showWaterfall = true;
    private bool _ready;
    private int _broadcastLimit;

    public HfLinkView(string directory, bool allowLive, Func<Task> returnToRadio, Action<HfMessage> received,
        Func<string, bool>? confirmTransmission = null)
    {
        _link = new HfLink(Path.Combine(directory, "hflink-native.sqlite3"), report: _session.Update);
        _allowLive = allowLive;
        _received = received;
        _confirmTransmission = confirmTransmission ?? Confirm;
        _bundled = new BundledMercury(directory);
        _bundled.ModemStopped += detail =>
        {
            _operation?.Cancel();
            Report(detail);
        };
        Dock = DockStyle.Fill;
        AccessibleName = "HFLink";
        Font = new Font("Segoe UI", 11);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 12, Padding = new Padding(16), AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
        Controls.Add(layout);
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(row switch
            {
                6 => _waterfallRow,
                8 => new RowStyle(SizeType.Percent, 100),
                9 => new RowStyle(SizeType.Absolute, 90),
                _ => new RowStyle(SizeType.AutoSize)
            });
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
        var heard = Button("Heard stations", ShowHeardStations);
        heard.Name = "hfLinkHeardStations";
        layout.Controls.Add(Row(_settings, heard, _audit), 0, 2);
        layout.Controls.Add(_station, 0, 3);

        _format.Items.AddRange(["HFLink", "Mercury ARQ Chat", "Mercury Broadcast"]);
        _format.SelectedIndex = 0;
        _destination.TextChanged += (_, _) =>
        {
            try { _format.SelectedIndex = (int)_link.ContactFormat(_destination.Text); }
            catch (ArgumentException) { _format.SelectedIndex = 0; }
        };
        _sendDraft = AsyncButton("Send…", SendDraftAsync);
        _sendDraft.Name = "hfLinkSendDraft";
        _message.PreviewKeyDown += (_, args) =>
        {
            if (args.KeyData == Keys.Enter) args.IsInputKey = true;
        };
        _message.KeyDown += (_, args) =>
        {
            if (args.KeyData != Keys.Enter) return;
            args.SuppressKeyPress = true;
            _sendDraft.PerformClick();
        };
        layout.Controls.Add(Row(FieldLabel(_destination), _destination, FieldLabel(_format), _format,
            FieldLabel(_message), _message, _bytes, _sendDraft), 0, 4);
        _message.TextChanged += (_, _) => UpdateMessageBytes();
        _format.SelectedIndexChanged += (_, _) =>
        {
            _destination.Enabled = _operation is null && (HfMessageFormat)_format.SelectedIndex != HfMessageFormat.MercuryBroadcast;
            UpdateMessageBytes();
        };

        _probe = AsyncButton("Check modem", () => RunAsync(async token =>
        {
            await PrepareModemAsync(transmission: false, token);
            await _link.ProbeAsync(_allowLive, token);
            return _link.Station().Simulation
                ? "Simulator ready. No RF is used."
                : "Mercury responded. TCP ports released after the check.";
        }, sessionPhase: "Checking modem"));
        _receive = AsyncButton("Open receive window", () => ReceiveAsync(broadcast: false));
        _receive.Name = "hfLinkReceive";
        _receiveBroadcast = AsyncButton("Listen for broadcasts", () => ReceiveAsync(broadcast: true));
        _receiveBroadcast.Name = "hfLinkReceiveBroadcast";
        _stop = Button("Stop operation", () => _operation?.Cancel());
        _stop.Name = "hfLinkStopOperation";
        _stopMercury = Button("Stop Mercury", () => { _bundled.Stop(); RefreshView(); Report("Bundled Mercury stopped."); });
        _waterfallToggle = Button("Show waterfall", () =>
        {
            _showWaterfall = !_showWaterfall;
            SetWaterfallVisible(_showWaterfall);
        });
        _waterfallToggle.Name = "hfLinkWaterfallToggle";
        _simulation.Items.AddRange(["Simulate no receipt", "Simulate receipt", "Simulate disconnect", "Simulate timeout"]);
        _simulation.Name = "hfLinkSimulationOutcome";
        _simulation.SelectedIndex = 0;
        layout.Controls.Add(Row(_probe, _receive, _receiveBroadcast, _stop, _stopMercury, _simulation, _waterfallToggle), 0, 5);
        layout.Controls.Add(_waterfall, 0, 6);
        layout.Controls.Add(_session, 1, 0);
        layout.SetRowSpan(_session, 7);

        _send = AsyncButton("Approve and send", SendAsync);
        _retry = Button("Retry", () => { _link.Retry(Selected().Id); RefreshView(); Report("Retry requires fresh approval."); });
        _cancel = Button("Cancel request", () => { _link.Cancel(Selected().Id); RefreshView(); Report("Request cancelled."); });
        _reply = Button("Reply", () =>
        {
            var incoming = Selected();
            _destination.Text = incoming.From;
            _format.SelectedIndex = (int)(incoming.Format == HfMessageFormat.MercuryBroadcast ? HfMessageFormat.MercuryChat : incoming.Format);
            _message.Focus();
        });
        _delete = Button("Delete message", DeleteSelectedMessage);
        _delete.Name = "hfLinkDeleteMessage";
        var messageActions = Row(FieldLabel(_filter), _filter, _send, _retry, _cancel, _reply, _delete);
        layout.Controls.Add(messageActions, 0, 7);
        _filter.TextChanged += (_, _) => RefreshView();
        foreach (var column in new[] { ("Time", 141), ("Mode", 85), ("Format", 125), ("Direction", 85), ("From", 95), ("To", 95), ("Status", 175) })
            _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Width = column.Item2, SortMode = DataGridViewColumnSortMode.NotSortable });
        _history.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        _history.CurrentCellChanged += (_, _) => { if (_ready) SetActions(); };
        layout.Controls.Add(_history, 0, 8);
        layout.Controls.Add(_detail, 0, 9);
        _receipt = Row(_receiptLabel, Button("Send receipt", () => _receiptDecision?.TrySetResult(true)),
            Button("Decline receipt", () => _receiptDecision?.TrySetResult(false)));
        _receipt.Visible = false;
        layout.Controls.Add(_receipt, 0, 10);
        layout.Controls.Add(_status, 0, 11);
        foreach (Control control in new Control[] { messageActions, _history, _detail, _receipt, _status })
            layout.SetColumnSpan(control, 2);
        if (!SystemInformation.HighContrast)
        {
            StyleView(layout);
            notice.ForeColor = Color.FromArgb(255, 204, 132);
        }
        _ready = true;
        RefreshView();
    }

    private void UpdateMessageBytes()
    {
        int limit = (HfMessageFormat)_format.SelectedIndex switch
        {
            HfMessageFormat.MercuryChat => HfPacket.MercuryChatTextLimit,
            HfMessageFormat.MercuryBroadcast => _broadcastLimit,
            _ => HfPacket.TextLimit
        };
        _message.MaxLength = limit;
        _bytes.Text = $"{Encoding.UTF8.GetByteCount(_message.Text)} / {limit} bytes";
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
        SetWaterfallVisible(false);
        try
        {
            using var dialog = CreateSettingsDialog();
            dialog.ShowDialog(this);
        }
        finally { EnterView(); }
    }

    private void SetWaterfallVisible(bool visible)
    {
        _waterfall.Visible = visible;
        _waterfallRow.Height = visible ? 158 : 0;
        _waterfallToggle.Text = visible ? "Hide waterfall" : "Show waterfall";
        if (!visible)
        {
            _waterfall.Stop();
            return;
        }
        var station = _link.Station();
        _waterfall.Start(station.Simulation || !_allowLive, () =>
        {
            string? path = station.UseBundledMercury ? _bundled.ConfigPath :
                _link.MercuryConfigPath() ?? MercurySettingsPanel.FindConfig();
            if (path is null || !File.Exists(path))
                throw new InvalidOperationException("Choose a receive input in HFLink Settings → Audio and PTT.");
            var config = MercurySettingsFile.Open(path);
            if (!config.Get("main", "sound_system").Equals("wasapi", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Save a Windows audio input in HFLink Settings → Audio and PTT.");
            string? endpoint = config.Get("main", "input_device", "\0");
            if (endpoint == "\0")
                throw new InvalidOperationException("Choose a receive input in HFLink Settings → Audio and PTT.");
            return endpoint;
        });
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
        var updatesTab = new TabPage("Mercury updates");
        var firewallTab = new TabPage("Firewall");
        tabs.TabPages.AddRange([stationTab, modemTab, updatesTab, firewallTab]);
        AddMercuryUpdates(updatesTab, dialog);
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
        var broadcastPort = Number(station.BroadcastPort, 1, 65535);
        var broadcastMode = new ComboBox { Name = "hfLinkBroadcastMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 255 };
        broadcastMode.Items.AddRange(MercuryBroadcast.Modes.Select(mode => (object)mode.Name).ToArray());
        broadcastMode.SelectedIndex = Math.Max(0, Array.FindIndex(MercuryBroadcast.Modes.ToArray(), mode => mode.Index == station.BroadcastMode));
        var timeout = Number(station.TimeoutSeconds, 5, 600);
        int[] bandwidths = [0, 500, 2300, 2750];
        var bandwidth = new ComboBox
        {
            Name = "hfLinkArqBandwidth", AccessibleName = "ARQ bandwidth", DropDownStyle = ComboBoxStyle.DropDownList, Width = 255
        };
        bandwidth.Items.AddRange(["Use modem setting", "500 Hz (narrow)", "2300 Hz", "2750 Hz"]);
        bandwidth.SelectedIndex = Math.Max(0, Array.IndexOf(bandwidths, station.ArqBandwidthHz));
        var bundled = new CheckBox { Text = "Use bundled Mercury", Checked = station.UseBundledMercury, AutoSize = true };
        void SetExternalFields() => host.Enabled = control.Enabled = data.Enabled = broadcastPort.Enabled = !bundled.Checked;
        bundled.CheckedChanged += (_, _) => SetExternalFields();
        SetExternalFields();
        var sim = new CheckBox { Text = "Simulation (no RF)", Checked = station.Simulation || !_allowLive, Enabled = _allowLive, AutoSize = true };
        var stationStatus = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };
        var save = new Button { Text = "Save station", AutoSize = true };
        var form = Fields([("Station callsign", call), ("Modem", bundled), ("External Mercury host", host), ("Control port", control),
            ("Data port", data), ("Broadcast port", broadcastPort), ("ARQ bandwidth", bandwidth), ("Broadcast mode", broadcastMode),
            ("Session timeout in seconds", timeout), ("Mode", sim), ("", save), ("", stationStatus)]);
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
                if (_operation is not null) throw new InvalidOperationException("Wait for the active HFLink operation to finish.");
                bool simulation = _allowLive ? sim.Checked : station.Simulation;
                _link.SaveStation(new(call.Text, host.Text, (int)control.Value, (int)data.Value, (int)timeout.Value,
                    simulation, bundled.Checked, bandwidths[bandwidth.SelectedIndex], (int)broadcastPort.Value,
                    MercuryBroadcast.Modes[broadcastMode.SelectedIndex].Index));
                station = _link.Station();
                _bundled.Stop();
                RefreshView();
                stationStatus.Text = "Station settings saved.";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { stationStatus.Text = error.Message; }
        };

        var firewallStatus = new Label { AutoSize = true, MaximumSize = new Size(565, 0) };
        var fix = new Button { Text = "Fix firewall", AutoSize = true, Enabled = _allowLive && _bundled.Installed };
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

    private void AddMercuryUpdates(TabPage tab, Form dialog)
    {
        var localVersion = new Label
        {
            Name = "hfLinkMercuryVersion", AutoSize = true,
            Text = MercuryVersionText()
        };
        var check = new Button { Name = "hfLinkMercuryUpdateCheck", Text = "Check for Mercury updates", AutoSize = true };
        var status = new Label { AutoSize = true, MaximumSize = new Size(565, 0), Text = "Compare the bundled modem with the latest stable Windows x64 release from Rhizomatica." };
        var instructions = new Label
        {
            AutoSize = true, MaximumSize = new Size(565, 0),
            Text = "HFLink can install updates for its bundled Mercury modem. Updates take effect the next time HFLink starts the modem."
        };
        Uri releasePage = MercuryReleaseClient.ReleasesPage;
        var details = new LinkLabel { Text = "View Mercury releases on GitHub", AutoSize = true };
        details.LinkClicked += (_, _) =>
        {
            try
            {
                using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(releasePage.AbsoluteUri)
                    { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { status.Text = "Windows could not open the Mercury release page."; }
        };
        var lifetime = new CancellationTokenSource();
        dialog.Disposed += CancelCheck;
        void CancelCheck(object? sender, EventArgs args)
        {
            dialog.Disposed -= CancelCheck;
            lifetime.Cancel();
            lifetime.Dispose();
        }
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            status.Text = "Checking Rhizomatica’s Mercury releases…";
            CancellationToken stop = lifetime.Token;
            try
            {
                using var http = UpdaterClient.CreateClient();
                var latest = await new MercuryReleaseClient(http).CheckAsync(stop);
                releasePage = latest.ReleasePage;
                if (dialog.IsDisposed) return;
                if (_link.Station().UseBundledMercury && CanInstallMercury(latest) && _operation is null)
                {
                    _offeredMercuryUpdate = latest.Version;
                    if (ConfirmMercuryUpdate(dialog, latest))
                    {
                        status.Text = $"Downloading and verifying Mercury {latest.Version}…";
                        await InstallMercuryAsync(latest, stop);
                        if (dialog.IsDisposed) return;
                        localVersion.Text = MercuryVersionText();
                    }
                }
                string result = await MercuryUpdateStatusAsync(latest, stop);
                if (dialog.IsDisposed) return;
                status.Text = $"Latest stable Windows x64 release · {latest.Version}\r\n{result}";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) when (IsMercuryUpdateError(error))
            {
                if (!dialog.IsDisposed) status.Text = $"Mercury update could not finish. {error.Message}";
            }
            finally { if (!dialog.IsDisposed) check.Enabled = true; }
        };
        tab.Controls.Add(Fields([("", localVersion), ("", check), ("", status), ("", details), ("", instructions)]));
    }

    private string MercuryVersionText() =>
        _bundled.Installed ? $"Bundled Mercury {_bundled.InstalledVersion}" : "Bundled Mercury is missing";

    private bool CanInstallMercury(MercuryWindowsRelease release) => release.Version > _bundled.InstalledVersion &&
        release.ArchiveSha256 is not null && release.ArchiveSize is > 0 and <= MercuryReleaseClient.MaximumArchiveBytes;

    private async Task<string> MercuryUpdateStatusAsync(MercuryWindowsRelease latest, CancellationToken stop)
    {
        if (!_link.Station().UseBundledMercury) return "External Mercury is selected. Its updates are managed by its own installation.";
        if (CanInstallMercury(latest)) return $"Mercury {latest.Version} is available to install.";
        if (!_bundled.Installed) return "Bundled Mercury is missing. Repair it with the MeshCore Command installer.";
        if (latest.Version < _bundled.InstalledVersion) return $"{MercuryVersionText()} is newer than the Windows release listed by GitHub.";
        if (latest.ArchiveSha256 is null || latest.Version > _bundled.InstalledVersion)
            return "GitHub did not provide a usable archive size and checksum.";
        await Task.Run(_bundled.VerifyInstalledFiles, stop);
        return _bundled.InstalledArchiveSha256.Equals(latest.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
            ? "Bundled Mercury is up to date." : "The upstream archive differs from the verified installation.";
    }

    private bool ConfirmMercuryUpdate(IWin32Window owner, MercuryWindowsRelease latest) =>
        MessageBox.Show(owner,
            $"Update bundled Mercury from {_bundled.InstalledVersion} to {latest.Version}? Your station settings will be kept. Mercury will stop if it is running and use the new version the next time it starts.",
            "Mercury update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private Task InstallMercuryAsync(MercuryWindowsRelease latest, CancellationToken stop) => RunAsync(async operation =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation, stop);
        using var http = UpdaterClient.CreateClient();
        await _bundled.InstallUpdateAsync(new MercuryReleaseClient(http), latest, linked.Token);
        return $"Mercury {latest.Version} installed. HFLink will use it the next time it starts the modem.";
    }, sessionPhase: $"Updating Mercury to {latest.Version}");

    private async Task CheckMercuryOnOpenAsync()
    {
        if (_checkingMercuryUpdate || _away || IsDisposed || !_link.Station().UseBundledMercury) return;
        _checkingMercuryUpdate = true;
        var stop = _viewLifetime.Token;
        try
        {
            using var http = UpdaterClient.CreateClient();
            var latest = await new MercuryReleaseClient(http).CheckAsync(stop);
            if (stop.IsCancellationRequested || _away || IsDisposed || _operation is not null ||
                Form.ActiveForm != FindForm() || !_link.Station().UseBundledMercury || !CanInstallMercury(latest) ||
                latest.Version == _offeredMercuryUpdate) return;
            _offeredMercuryUpdate = latest.Version;
            if (ConfirmMercuryUpdate(this, latest)) await InstallMercuryAsync(latest, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (IsMercuryUpdateError(error))
        {
            if (!_away && _operation is null) Report($"Mercury update could not finish. {error.Message}");
        }
        finally { _checkingMercuryUpdate = false; }
    }

    private static bool IsMercuryUpdateError(Exception error) =>
        error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException;

    public void EnterView()
    {
        bool opening = _away;
        _away = false;
        RefreshView();
        if (_showWaterfall && !_waterfall.Visible) SetWaterfallVisible(true);
        if (!_allowLive || _operation is not null) return;
        BeginInvoke(async () =>
        {
            if (_bundled.Installed) await OfferFirewallSetupAsync();
            if (opening) await CheckMercuryOnOpenAsync();
        });
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
        if (!ConfirmSend(message.From, message.To, message.Text, message.Format, message.Simulated)) return;
        await SendApprovedAsync(message.Id, message.To, message.Format);
    }

    private async Task SendDraftAsync()
    {
        var format = (HfMessageFormat)_format.SelectedIndex;
        string text = _message.Text;
        var (station, destination) = _link.ValidateDraft(_destination.Text, text, format);
        if (!ConfirmSend(station.Callsign, destination, text, format, station.Simulation)) return;
        Guid id = _link.Queue(destination, text, format);
        _message.Clear();
        await SendApprovedAsync(id, destination, format);
    }

    private bool ConfirmSend(string from, string to, string text, HfMessageFormat format, bool simulated)
    {
        string question = format == HfMessageFormat.MercuryBroadcast
            ? $"Broadcast this message from {from} to stations listening on this frequency and mode?"
            : $"Transmit this {FormatText(format)} message from {from} to {to}?";
        string simulation = simulated ? "\n\nSimulation is on. No RF will be used." : "";
        return _confirmTransmission($"{question}\n\n{text}{simulation}");
    }

    private Task SendApprovedAsync(Guid id, string destination, HfMessageFormat format) =>
        RunAsync(async token =>
        {
            await PrepareModemAsync(transmission: true, token);
            return StatusText(await _link.ApproveAndSendAsync(id, (HfSimulation)_simulation.SelectedIndex, _allowLive, token));
        }, sessionPhase: format == HfMessageFormat.MercuryBroadcast ? "Preparing broadcast" : $"Preparing to send to {destination}");

    private async Task ReceiveAsync(bool broadcast)
    {
        var station = _link.Station();
        if (!broadcast && !station.Simulation && !Confirm($"Open a receive window for {station.Callsign} for up to {station.TimeoutSeconds} seconds? Mercury may transmit ARQ control frames while listening.")) return;
        await RunAsync(async token =>
        {
            await PrepareModemAsync(transmission: !broadcast, token);
            Report(broadcast ? "Listening for broadcasts. You can return to the radio page while listening." :
                "Receive window open. You can return to the radio page while listening.");
            int received = 0;
            await _link.ReceiveAsync(_allowLive, ApproveReceiptAsync, token, message =>
            {
                received++;
                RefreshView();
                _received(message);
            }, broadcast);
            return received switch
            {
                0 => "Receive window closed.",
                1 => "Message received. Receive window closed.",
                _ => $"{received} messages received. Receive window closed."
            };
        }, receiving: true, sessionPhase: broadcast ? "Opening broadcast receive window" : "Opening receive window");
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> action, bool receiving = false, string? sessionPhase = null)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _finished = finished;
        _operation = operation;
        _receiving = receiving;
        if (sessionPhase is not null) _session.Start(sessionPhase);
        try
        {
            Report("Modem operation in progress…");
            var pending = action(operation.Token);
            RefreshView();
            string outcome = await pending;
            Report(outcome);
            if (sessionPhase is not null) _session.Update(new(outcome));
        }
        catch (OperationCanceledException)
        {
            string outcome = operation.IsCancellationRequested ? "Modem operation stopped." : "Mercury operation timed out.";
            Report(outcome);
            if (sessionPhase is not null) _session.Update(new(outcome));
        }
        catch (Exception error)
        {
            if (sessionPhase is not null) _session.Update(new(error is Microsoft.Data.Sqlite.SqliteException ? "HFLink storage operation failed." : error.Message));
            throw;
        }
        finally
        {
            _operation = null;
            _finished = null;
            _receiving = false;
            if (sessionPhase is not null) _session.Finish();
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
        if (!_receiving) return StopAsync();
        SetWaterfallVisible(false);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        SetWaterfallVisible(false);
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
            if (!IsDisposed) { _viewLifetime.Cancel(); _viewLifetime.Dispose(); }
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
        _session.Update(new("Message saved. Receipt awaiting approval.", Peer: message.From));
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
        _heardStationsWindow?.SetStations(_link.HeardStations(station.Simulation));
        _broadcastLimit = MercuryBroadcast.TextLimit(station.Callsign, station.BroadcastMode);
        UpdateMessageBytes();
        string mode = station.Simulation ? "simulation" : station.UseBundledMercury ? "bundled Mercury" : "external Mercury";
        string bundledVersion = station.UseBundledMercury && _bundled.Installed ? $"   Mercury {_bundled.InstalledVersion}" : "";
        string arqLimit = station.ArqBandwidthHz == 0 ? "" : $"   ARQ limit {station.ArqBandwidthHz} Hz";
        _station.Text = $"Station: {(station.Callsign.Length == 0 ? "not configured" : station.Callsign)}   Mode: {mode}{bundledVersion}{arqLimit}";
        var filter = _filter.Text.Trim().ToUpperInvariant();
        foreach (var message in _link.Messages().Where(m => filter.Length == 0 || m.From.Contains(filter) || m.To.Contains(filter)))
        {
            int index = _history.Rows.Add(message.Created.ToLocalTime().ToString("g"), message.Simulated ? "Simulation" : "Mercury",
                FormatText(message.Format), message.Incoming ? "Incoming" : "Outgoing", message.From, message.To,
                StatusText(message.Status), message.Text);
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
        _sendDraft.Enabled = idle && configured && liveAllowed;
        _format.Enabled = idle;
        _destination.Enabled = idle && (HfMessageFormat)_format.SelectedIndex != HfMessageFormat.MercuryBroadcast;
        _receive.Enabled = _receiveBroadcast.Enabled = _probe.Enabled = idle && configured && liveAllowed;
        _stop.Enabled = !idle;
        _simulation.Visible = station.Simulation;
        _simulation.Enabled = idle && station.Simulation;
        _send.Enabled = idle && configured && liveAllowed && message is { Incoming: false, Status: HfStatus.AwaitingApproval };
        _cancel.Enabled = idle && message is { Incoming: false, Status: HfStatus.AwaitingApproval };
        _retry.Enabled = idle && message is { Incoming: false, Status: HfStatus.Failed or HfStatus.Unconfirmed };
        _reply.Enabled = idle && configured && message is { Incoming: true };
        _delete.Enabled = idle && message is not null;
        _detail.Text = message is null ? "" : message.Text + Environment.NewLine + message.Detail +
            (message.Incoming ? "" : $"\r\nRequested by {message.Requester} ({message.RequesterCall})\r\nID: {message.Id}");
    }

    private void DeleteSelectedMessage()
    {
        var message = Selected();
        string direction = message.Incoming ? $"received message from {message.From}" : $"outgoing message to {message.To}";
        if (MessageBox.Show(this, $"Permanently delete the {direction} from HFLink on this PC?",
            "Delete HFLink message", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _link.DeleteMessage(message);
        RefreshView();
        Report("Message deleted.");
    }

    private void ShowHeardStations()
    {
        bool simulated = _link.Station().Simulation;
        using var window = new HfHeardStationsWindow(simulated);
        window.SetStations(_link.HeardStations(simulated));
        _heardStationsWindow = window;
        try
        {
            if (window.ShowDialog(this) != DialogResult.OK || window.SelectedCallsign is not { } callsign) return;
            if (window.DraftFormat is not { } format)
            {
                _filter.Text = callsign;
                _history.Focus();
                return;
            }
            _destination.Text = callsign;
            _format.SelectedIndex = (int)format;
            if (format == HfMessageFormat.MercuryBroadcast)
            {
                string prefix = callsign + ", ";
                if (!_message.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _message.Text = prefix + _message.Text;
            }
            _message.SelectionStart = _message.TextLength;
            _message.Focus();
        }
        finally { _heardStationsWindow = null; }
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
    internal static string FormatText(HfMessageFormat format) => format switch
    {
        HfMessageFormat.MercuryChat => "Mercury ARQ Chat",
        HfMessageFormat.MercuryBroadcast => "Mercury Broadcast",
        _ => "HFLink"
    };
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
