using System.IO.Ports;
using NAudio.CoreAudioApi;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal sealed class HfJs8SettingsDialog : Form
{
    private sealed record Device(string Id, string Name) { public override string ToString() => Name; }
    private sealed record Speed(int Value, string Name) { public override string ToString() => Name; }
    private static readonly Speed[] Speeds = [new(0, "Normal"), new(4, "Slow"), new(1, "Fast"), new(2, "Turbo"), new(8, "Ultra")];
    internal Js8LinkSettings Settings { get; private set; }

    internal HfJs8SettingsDialog(Js8LinkSettings settings)
    {
        Settings = settings;
        var station = settings.Station;
        Text = "HFLink settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(660, 640);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 195));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var call = new TextBox { Text = station?.Callsign ?? "", MaxLength = 15 };
        var grid = new TextBox { Text = station?.Grid ?? "", MaxLength = 6 };
        var input = AudioDevices(DataFlow.Capture, station?.Input);
        var output = AudioDevices(DataFlow.Render, station?.Output);
        var ptt = Choices(["VOX", "RTS", "DTR", "CAT"]);
        ptt.SelectedItem = station?.Ptt;
        var serial = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = station?.SerialPort ?? "" };
        serial.Items.AddRange(SerialPort.GetPortNames().Order().Cast<object>().ToArray());
        serial.Enabled = ptt.SelectedItem is "RTS" or "DTR";
        ptt.SelectedIndexChanged += (_, _) => serial.Enabled = ptt.SelectedItem is "RTS" or "DTR";
        var cat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = station?.CatPort ?? "" };
        cat.Items.AddRange(serial.Items.Cast<object>().ToArray());
        var baud = Choices([4800, 9600, 19200, 38400]);
        baud.SelectedItem = station?.CatBaud ?? 4800;
        var dial = DialInput(station?.DialHz ?? 14_078_000);
        var delay = new NumericUpDown { Minimum = .1m, Maximum = 2, Value = station?.TxDelay ?? .2m, DecimalPlaces = 2, Increment = .05m };
        var speed = Choices(Speeds);
        speed.SelectedItem = Speeds.Single(item => item.Value == (station?.Speed ?? 0));
        var offset = new NumericUpDown { Minimum = 100, Maximum = 3500, Value = station?.Offset ?? 1500, Increment = 10 };
        var group = new TextBox { Text = settings.PublicGroup, MaxLength = 9 };
        var relay = new TextBox { Text = settings.Relay, MaxLength = 15 };
        var auto = new CheckBox { Text = "Allow JS8Call to transmit automatic replies", AutoSize = true, Checked = station?.AutoReply ?? false };
        foreach (var (caption, control) in new (string, Control)[]
        {
            ("Station callsign", call), ("Maidenhead locator", grid), ("Radio audio input", input), ("Radio audio output", output),
            ("PTT method", ptt), ("PTT COM port", serial), ("FT-450 CAT COM port", cat), ("CAT baud rate", baud),
            ("Dial frequency (MHz)", dial), ("PTT delay (seconds)", delay), ("JS8 speed", speed),
            ("Transmit audio offset (Hz)", offset), ("Public group", group), ("Inbox relay callsign", relay), ("Automatic replies", auto)
        })
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = caption, Anchor = AnchorStyles.Left, AutoSize = true });
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.AccessibleName = caption;
            layout.Controls.Add(control);
        }
        var note = new Label { Text = "Leave CAT port blank for manual tuning. Set USB mode on the radio.", AutoSize = true, Margin = new Padding(3, 8, 3, 4) };
        layout.Controls.Add(note);
        layout.SetColumnSpan(note, 2);
        var save = new HfLinkTheme.ActionButton { Text = "Save and start modem", AutoSize = true };
        var cancel = new HfLinkTheme.ActionButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange([save, cancel]);
        layout.Controls.Add(actions);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        CancelButton = cancel;
        AcceptButton = save;
        save.Click += (_, _) =>
        {
            try
            {
                Settings = (settings with { PublicGroup = group.Text, Relay = relay.Text,
                    Station = new(call.Text, grid.Text, (input.SelectedItem as Device)?.Id ?? "", (output.SelectedItem as Device)?.Id ?? "",
                        ptt.SelectedItem as string ?? "", serial.Text, delay.Value, ((Speed)speed.SelectedItem!).Value, (int)offset.Value, auto.Checked,
                        (int)(dial.Value * 1_000_000), cat.Text, (int)baud.SelectedItem!) }).Validate();
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        HfLinkTheme.Apply(this);
    }

    internal static NumericUpDown DialInput(int hz) => new()
    {
        Minimum = .1m, Maximum = 56m, DecimalPlaces = 6, Increment = .001m,
        Value = hz / 1_000_000m, Width = 140, AccessibleName = "Dial frequency in MHz"
    };

    private static ComboBox Choices(object[] items)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        box.Items.AddRange(items);
        return box;
    }

    private static ComboBox AudioDevices(DataFlow flow, string? selection)
    {
        using var enumerator = new MMDeviceEnumerator();
        var items = new List<Device>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) items.Add(new Device(device.ID, device.FriendlyName));
        }
        if (!string.IsNullOrEmpty(selection) && items.All(item => item.Id != selection))
            items.Add(new Device(selection, "Previously selected device (unavailable)"));
        var box = Choices(items.OrderBy(item => item.Name).Cast<object>().ToArray());
        box.SelectedItem = items.FirstOrDefault(item => item.Id == selection);
        return box;
    }
}
