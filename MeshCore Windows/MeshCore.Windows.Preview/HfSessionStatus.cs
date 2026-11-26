using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal sealed class HfSessionStatus : Panel
{
    private readonly Label _phase = new() { Name = "hfLinkSessionPhase", AutoSize = true, MaximumSize = new Size(198, 0) };
    private readonly Label _details = new() { AutoSize = true };
    private HfModemStatus _current = new("Idle");
    private bool _active;

    public HfSessionStatus()
    {
        Name = "hfLinkSessionStatus";
        AccessibleName = "HFLink session status";
        Dock = DockStyle.Fill;
        Margin = new Padding(12, 3, 0, 3);
        Padding = new Padding(9);
        BorderStyle = BorderStyle.FixedSingle;
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false
        };
        content.Controls.AddRange([
            new Label { Text = "Session status", AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 11, FontStyle.Bold) },
            _phase, _details
        ]);
        Controls.Add(content);
        RenderStatus();
    }

    public void Start(string phase)
    {
        if (IsDisposed) return;
        _active = true;
        _current = new(phase);
        RenderStatus();
    }

    public void Update(HfModemStatus change)
    {
        if (IsDisposed) return;
        _current = new(change.Phase ?? _current.Phase, change.Peer ?? _current.Peer,
            change.Keyed ?? _current.Keyed, change.Busy ?? _current.Busy, change.Snr ?? _current.Snr,
            change.Bitrate ?? _current.Bitrate, change.Bandwidth ?? _current.Bandwidth, change.QueuedBytes ?? _current.QueuedBytes);
        RenderStatus();
    }

    public void Finish()
    {
        if (IsDisposed) return;
        _active = false;
        RenderStatus();
    }

    private void RenderStatus()
    {
        _phase.Text = _current.Phase ?? "Idle";
        _details.Text = string.Join(Environment.NewLine,
            $"Station  {_current.Peer ?? "—"}",
            $"PTT  {(_active ? State(_current.Keyed, "TX", "Off") : "—")}   Channel  {(_active ? State(_current.Busy, "Busy", "Clear") : "—")}",
            $"SNR  {(_current.Snr is { } snr ? $"{snr:0.0} dB" : "—")}",
            $"Bitrate  {(_current.Bitrate is { } rate ? $"{rate} bps" : "—")}",
            $"Bandwidth  {(_current.Bandwidth is { } width ? $"{width} Hz" : "—")}",
            $"TX queue  {(_active && _current.QueuedBytes is { } size ? $"{size} B" : "—")}");

        static string State(bool? value, string yes, string no) => value switch { true => yes, false => no, _ => "—" };
    }
}
