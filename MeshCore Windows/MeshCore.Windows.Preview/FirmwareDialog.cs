using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

/// <summary>Development-only firmware dialog. Hardware qualification is still required.</summary>
internal sealed class FirmwareDialog : Form
{
    private sealed record Choice(string Label, string Id) { public override string ToString() => Label; }
    private readonly ComboBox _board = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
    private readonly ComboBox _port = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly TextBox _ssid = new() { Width = 310 };
    private readonly TextBox _password = new() { Width = 310, UseSystemPasswordChar = true };
    private readonly Button _flash = new() { Text = "Flash and configure", AutoSize = true };
    private readonly Button _refresh = new() { Text = "Refresh ports", AutoSize = true };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 310 };
    private readonly Label _status = new() { Text = "Experimental: use a dedicated test board.", AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Func<string, bool> _portInUse;
    private bool _busy;
    public bool IsBusy => _busy;

    public FirmwareDialog(Func<string, bool> portInUse)
    {
        _portInUse = portInUse; Text = "Firmware — development preview"; Width = 480; Height = 430;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var choices = new[] { new Choice("Heltec V3 — no screen", "v3-headless"), new Choice("Heltec V3 — OLED", "v3-oled"),
            new Choice("Heltec V4 — automatic display", "v4-auto"), new Choice("Heltec V4 — TFT", "v4-tft"),
            new Choice("Heltec V4 R8 — no screen", "r8-headless"), new Choice("Heltec V4 R8 — OLED", "r8-oled"),
            new Choice("Wireless Tracker V2 — no screen", "tracker-headless"), new Choice("Wireless Tracker V2 — screen", "tracker-screen"),
            new Choice("T-Beam S3 Supreme SX1262", "tbeam-oled") };
        _board.Items.AddRange(choices); _board.SelectedIndex = 0;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18), AutoScroll = true };
        layout.Controls.Add(new Label { Text = "Board and display", AutoSize = true }); layout.Controls.Add(_board);
        layout.Controls.Add(new Label { Text = "USB serial port", AutoSize = true });
        var ports = new FlowLayoutPanel { AutoSize = true }; ports.Controls.Add(_port); ports.Controls.Add(_refresh); layout.Controls.Add(ports);
        layout.Controls.Add(new Label { Text = "Wi-Fi network (2.4 GHz)", AutoSize = true }); layout.Controls.Add(_ssid);
        layout.Controls.Add(new Label { Text = "Wi-Fi password", AutoSize = true }); layout.Controls.Add(_password);
        layout.Controls.Add(_progress); layout.Controls.Add(_status); layout.Controls.Add(_flash); Controls.Add(layout);
        _refresh.Click += (_, _) => RefreshPorts(); _flash.Click += async (_, _) => await FlashAsync();
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Wait until the firmware operation finishes."; } };
        RefreshPorts();
    }

    private void RefreshPorts()
    {
        string? previous = _port.SelectedItem as string; _port.Items.Clear(); _port.Items.AddRange(UsbGps.Ports());
        if (previous is not null && _port.Items.Contains(previous)) _port.SelectedItem = previous; else if (_port.Items.Count > 0) _port.SelectedIndex = 0;
    }

    private async Task FlashAsync()
    {
        if (_busy) return;
        string password = _password.Text;
        try
        {
            if (_board.SelectedItem is not Choice choice || _port.SelectedItem is not string port) throw new ArgumentException("Choose the exact board and USB port.");
            FirmwareCatalog.ValidateWifiCredentials(_ssid.Text, password);
            if (_portInUse(port)) throw new InvalidOperationException("Disconnect this USB radio in the preview before flashing it.");
            string firmware = Path.Combine(AppContext.BaseDirectory, "firmware"), tool = Path.Combine(AppContext.BaseDirectory, "espflash.exe");
            _ = FirmwareCatalog.Verify(firmware, choice.Id);
            if (!File.Exists(tool)) throw new FileNotFoundException("The verified native flasher is not installed. Run Fetch-EspFlash.ps1 before building.");
            if (MessageBox.Show(this, "Flash the selected test board? Its firmware and partition layout will be replaced, and its identity or settings may not carry over.", "Confirm test flash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _busy = true; _flash.Enabled = false; _refresh.Enabled = false; _board.Enabled = false; _port.Enabled = false;
            var updates = new Progress<FlashProgress>(item => { _progress.Value = Math.Clamp(item.Percent, 0, 100); _status.Text = item.Message; });
            await new EspFlashExecutor(new EspFlashTool(tool)).FlashAsync(firmware, choice.Id, port, UsbGps.Ports(), updates);
            _status.Text = "Firmware verified. Configuring Wi-Fi…";
            await Task.Delay(1000);
            var address = await UsbWifiProvisioner.ConfigureAsync(port, _ssid.Text, password);
            _status.Text = "Wi-Fi configured. Radio address: " + address; _progress.Value = 100;
        }
        catch (Exception error) { _status.Text = error.Message; }
        finally { _password.Clear(); _busy = false; _flash.Enabled = true; _refresh.Enabled = true; _board.Enabled = true; _port.Enabled = true; }
    }
}
