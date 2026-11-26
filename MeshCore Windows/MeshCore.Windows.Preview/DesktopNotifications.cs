namespace MeshCore.Windows.Preview;

/// <summary>Non-activating desktop alerts that remain visible when Windows suppresses tray balloons.</summary>
internal sealed class DesktopNotifications : IDisposable
{
    private readonly Form _owner;
    private readonly Action _activate;
    private readonly List<AlertWindow> _alerts = new();
    private bool _disposed;

    public DesktopNotifications(Form owner, Action activate) { _owner = owner; _activate = activate; }

    public void Show(string title, string body, Action? activate = null)
    {
        if (_disposed) return;
        while (_alerts.Count >= 3) _alerts[0].Close();
        var alert = new AlertWindow(title, body, activate ?? _activate);
        _alerts.Add(alert);
        alert.FormClosed += (_, _) => { _alerts.Remove(alert); PositionAlerts(); };
        PositionAlerts();
        alert.Show();
    }

    private void PositionAlerts()
    {
        Rectangle work = _owner.IsDisposed ? Screen.PrimaryScreen!.WorkingArea : Screen.FromControl(_owner).WorkingArea;
        int bottom = work.Bottom - 12;
        foreach (var alert in _alerts.AsEnumerable().Reverse())
        { bottom -= alert.Height; alert.Location = new Point(work.Right - alert.Width - 12, bottom); bottom -= 8; }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var alert in _alerts.ToArray()) alert.Close();
        _alerts.Clear();
    }

    private sealed class AlertWindow : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10000 };
        public AlertWindow(string title, string body, Action activate)
        {
            Text = "MeshCore Command notification"; FormBorderStyle = FormBorderStyle.FixedSingle;
            ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
            BackColor = Color.FromArgb(19, 31, 42); ForeColor = Color.White; ClientSize = new Size(360, 105);
            MaximizeBox = false; MinimizeBox = false; ControlBox = false;
            var heading = new Label { Text = Trim(title, 120), AutoEllipsis = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Location = new Point(14, 12), Size = new Size(330, 22) };
            var message = new Label { Text = Trim(body, 240), AutoEllipsis = true, Location = new Point(14, 39), Size = new Size(330, 54) };
            Controls.Add(heading); Controls.Add(message);
            void Open(object? _, EventArgs __) { Close(); activate(); }
            Click += Open; heading.Click += Open; message.Click += Open;
            _timer.Tick += (_, _) => Close(); Shown += (_, _) => _timer.Start();
            FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } }
        private static string Trim(string? value, int maximum) { value = (value ?? "").Trim(); return value.Length <= maximum ? value : value[..maximum]; }
    }
}
