using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using MeshCore.Windows.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace MeshCore.Windows.Preview;

internal sealed class MercurySettingsDialog : Form
{
    private sealed record AudioChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly Regex EndpointId = new(@"\{0\.0\.[01]\.00000000\}\.\{[a-fA-F0-9-]{36}\}");
    private readonly Func<MercurySettingsFile, string?> _saveConfig;
    private readonly bool _fixedPath;
    private readonly Label _file = new() { AutoSize = true, MaximumSize = new Size(590, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(590, 0) };
    private readonly ComboBox _input = Choice(), _output = Choice();
    private readonly ComboBox _method = Choice("none", "serial", "hamlib", "cm108", "hermes_shm");
    private readonly ComboBox _line = Choice("rts", "dtr", "both");
    private readonly ComboBox _invert = Choice("none", "rts", "dtr", "both");
    private readonly ComboBox _device = new() { Width = 310, MaxLength = 240 };
    private readonly TextBox _model = new() { Width = 310, MaxLength = 12 };
    private readonly TextBox _baud = new() { Width = 310, MaxLength = 10 };
    private readonly TextBox _delay = new() { Width = 310, MaxLength = 4 };
    private readonly Button _save = new() { Text = "Save settings", AutoSize = true, Enabled = false };
    private MercurySettingsFile? _config;
    private AudioChoice[] _inputs = [], _outputs = [];
    public bool Saved { get; private set; }

    public MercurySettingsDialog(string? path, Func<MercurySettingsFile, string?> saveConfig, bool fixedPath = false)
    {
        _saveConfig = saveConfig;
        _fixedPath = fixedPath;
        Text = "HFLink — Mercury settings";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(690, 540);
        Size = new Size(720, 570);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(590, 0),
            Text = fixedPath ? "Select audio and push-to-talk for bundled Mercury. Set frequency and sideband on the radio." :
                "Select the INI file used to start your local Mercury. Restart Mercury after saving. Set frequency and sideband on the radio."
        });
        layout.Controls.Add(_file);
        var browse = new Button { Text = "Open INI…", AutoSize = true };
        browse.Click += (_, _) => ChooseFile(create: false);
        var create = new Button { Text = "New INI…", AutoSize = true };
        create.Click += (_, _) => ChooseFile(create: true);
        if (!fixedPath) layout.Controls.Add(Row(browse, create));

        var fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        (string Caption, Control Control)[] rows =
        [
            ("Receive audio input", _input), ("Transmit audio output", _output), ("PTT method", _method),
            ("PTT device (COM port or Hamlib endpoint)", _device), ("Serial PTT line", _line), ("Invert serial line", _invert),
            ("Hamlib model ID", _model), ("Hamlib baud rate (0 = default)", _baud), ("TX keying delay (milliseconds)", _delay)
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            fields.Controls.Add(new Label { Text = rows[i].Caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            rows[i].Control.AccessibleName = rows[i].Caption;
            fields.Controls.Add(rows[i].Control, 1, i);
        }
        layout.Controls.Add(fields);
        layout.Controls.Add(_status);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        layout.Controls.Add(Row(_save, cancel));
        CancelButton = cancel;
        _save.Click += (_, _) => Save();
        _method.SelectedIndexChanged += (_, _) => SetPttControls();
        _device.Items.AddRange(SerialPort.GetPortNames().Order().Cast<object>().ToArray());
        LoadConfig(path);
        Shown += async (_, _) =>
        {
            try
            {
                var inputTask = DevicesAsync(MediaDevice.GetAudioCaptureSelector());
                var outputTask = DevicesAsync(MediaDevice.GetAudioRenderSelector());
                await Task.WhenAll(inputTask, outputTask);
                if (IsDisposed) return;
                _inputs = await inputTask;
                _outputs = await outputTask;
                LoadAudioChoices();
            }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                if (!IsDisposed) _status.Text = "Windows audio devices could not be listed. Existing audio settings can still be kept.";
            }
        };
    }

    public static string? FindConfig()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates = [
            Path.Combine(local, "Programs", "Mercury-HFLink-1.9.25", "mercury-1.9.25", "mercury.ini"),
            Path.Combine(local, "Programs", "Mercury", "mercury.ini"),
            Path.Combine(local, "Mercury", "mercury.ini"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mercury", "mercury.ini")
        ];
        var existing = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return existing.Length == 1 ? existing[0] : null;
    }

    private static async Task<AudioChoice[]> DevicesAsync(string selector)
    {
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.Select(device => new AudioChoice(EndpointId.Match(device.Id).Value, device.Name))
            .Where(device => device.Id.Length > 0).DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(device => device.Name).ToArray();
    }

    private void ChooseFile(bool create)
    {
        using FileDialog picker = create
            ? new SaveFileDialog { OverwritePrompt = true, FileName = "mercury.ini" }
            : new OpenFileDialog { CheckFileExists = true };
        picker.Filter = "Mercury INI files (*.ini)|*.ini";
        picker.DefaultExt = "ini";
        if (_config is not null) picker.InitialDirectory = Path.GetDirectoryName(_config.Path)!;
        if (picker.ShowDialog(this) == DialogResult.OK) LoadConfig(picker.FileName);
    }

    private void LoadConfig(string? path)
    {
        _config = null;
        _save.Enabled = false;
        _file.Text = path ?? "Choose a Mercury INI file.";
        _status.Text = "";
        try
        {
            if (path is null) return;
            _config = MercurySettingsFile.Open(path);
            var config = _config;
            string legacyModel = config.Get("main", "radio_model", "-1");
            string method = config.Get("ptt", "method", legacyModel is "-1" or "" ? "none" : "hamlib");
            Select(_method, method);
            _device.Text = config.Get("ptt", "device", config.Get("main", "radio_device"));
            Select(_line, config.Get("ptt", "line", "rts"));
            Select(_invert, config.Get("ptt", "invert", "none"));
            _model.Text = config.Get("ptt", "hamlib_model", legacyModel);
            _baud.Text = config.Get("ptt", "hamlib_serial_speed", config.Get("main", "radio_serial_speed", "0"));
            _delay.Text = config.Get("audio", "tx_delay_ms", "10");
            _input.SelectedIndex = _output.SelectedIndex = -1;
            LoadAudioChoices();
            _save.Enabled = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { _status.Text = error.Message; }
    }

    private void LoadAudioChoices()
    {
        SetAudioChoices(_input, _inputs, "input");
        SetAudioChoices(_output, _outputs, "output");
    }

    private void SetAudioChoices(ComboBox box, AudioChoice[] choices, string key)
    {
        var selected = box.SelectedItem as AudioChoice;
        box.Items.Clear();
        string current = _config?.Get("main", key + "_device_name") ?? "";
        if (current.Length == 0) current = _config?.Get("main", key + "_device") ?? "";
        box.Items.Add(current.Length == 0 ? "Keep existing settings (system default)" : $"Keep {current}");
        box.Items.Add(new AudioChoice("", "Default Windows device (WASAPI)"));
        box.Items.AddRange(choices.Cast<object>().ToArray());
        box.SelectedItem = selected is null ? null : box.Items.OfType<AudioChoice>().FirstOrDefault(option => option.Id == selected.Id);
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private void SetPttControls()
    {
        string? method = _method.SelectedItem as string;
        _device.Enabled = method is "serial" or "serial_rts" or "hamlib";
        _line.Enabled = _invert.Enabled = method is "serial" or "serial_rts";
        _model.Enabled = _baud.Enabled = method == "hamlib";
    }

    private void Save()
    {
        if (_config is null) return;
        try
        {
            var config = _config.BeginEdit();
            string method = (string)_method.SelectedItem!;
            string device = _device.Text.Trim();
            var input = _input.SelectedItem as AudioChoice;
            var output = _output.SelectedItem as AudioChoice;
            if (_fixedPath && ((input is null && config.Get("main", "input_device", "\0") == "\0") ||
                (output is null && config.Get("main", "output_device", "\0") == "\0")))
                throw new ArgumentException("Choose both audio devices for bundled Mercury.");
            if ((input is not null || output is not null) && (input is null || output is null) &&
                !config.Get("main", "sound_system").Equals("wasapi", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose both audio devices when switching Mercury to WASAPI.");
            if (method is "serial" or "serial_rts" && !Regex.IsMatch(device, @"\ACOM[1-9][0-9]*\z", RegexOptions.IgnoreCase))
                throw new ArgumentException("Choose a Windows COM port for serial PTT.");
            if (method == "hamlib" && device.Length == 0)
                throw new ArgumentException("Enter the Hamlib COM port or endpoint.");
            if (method == "hamlib")
            {
                Put(config, "ptt", "hamlib_model", Integer(_model.Text, "Hamlib model ID", 1, int.MaxValue));
                Put(config, "ptt", "hamlib_serial_speed", Integer(_baud.Text, "Hamlib baud rate", 0, int.MaxValue));
            }
            string delay = Integer(_delay.Text, "TX keying delay", 0, 2000);
            Put(config, "ptt", "method", method);
            if (_device.Enabled) Put(config, "ptt", "device", device);
            if (_line.Enabled)
            {
                Put(config, "ptt", "line", (string)_line.SelectedItem!);
                Put(config, "ptt", "invert", (string)_invert.SelectedItem!);
            }
            Put(config, "audio", "tx_delay_ms", delay);
            SaveAudio(config, input, "input");
            SaveAudio(config, output, "output");
            string? backup = _saveConfig(config);
            Saved = true;
            if (backup is not null)
                MessageBox.Show(this, (_fixedPath ? "HFLink will use these settings the next time it starts Mercury. " :
                    "Restart Mercury to apply these settings. ") + $"Backup saved as {Path.GetFileName(backup)}.",
                    "Mercury settings saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status.Text = error.Message;
        }
    }

    private static void SaveAudio(MercurySettingsFile config, AudioChoice? choice, string key)
    {
        if (choice is null) return;
        Put(config, "main", "sound_system", "wasapi");
        Put(config, "main", key + "_device", choice.Id);
        Put(config, "main", key + "_device_name", choice.Id.Length == 0 ? "" : choice.Name);
    }

    private static void Put(MercurySettingsFile config, string section, string key, string value)
    {
        if (config.Get(section, key, "\0") != value) config.Set(section, key, value);
    }

    private static string Integer(string value, string label, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"{label} must be between {min} and {max}.");
        return parsed.ToString(CultureInfo.InvariantCulture);
    }

    private static void Select(ComboBox box, string value)
    {
        if (!box.Items.Contains(value)) box.Items.Add(value);
        box.SelectedItem = value;
    }

    private static ComboBox Choice(params string[] values)
    {
        var box = new ComboBox { Width = 310, DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(values);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.AddRange(controls);
        return row;
    }
}
