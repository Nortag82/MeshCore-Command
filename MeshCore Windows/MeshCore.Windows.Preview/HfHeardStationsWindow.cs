using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal sealed class HfHeardStationsWindow : Form
{
    private readonly DataGridView _stations = new()
    {
        Name = "hfLinkHeardStationsList", AccessibleName = "Heard stations", Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Button _broadcast, _direct, _history;
    public string? SelectedCallsign { get; private set; }
    public HfMessageFormat? DraftFormat { get; private set; }

    public HfHeardStationsWindow(bool simulated)
    {
        Text = simulated ? "HFLink heard stations · Simulation" : "HFLink heard stations";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(660, 450);
        MinimumSize = new Size(565, 320);
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_stations, 0, 1);
        foreach (var (heading, width) in new[] { ("Callsign", 130), ("Last heard", 170), ("Last format", 160) })
            _stations.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = heading, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        _broadcast = ActionButton("Reply by broadcast", HfMessageFormat.MercuryBroadcast);
        _direct = ActionButton("Message via ARQ", HfMessageFormat.MercuryChat);
        _history = ActionButton("View history", null);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([_broadcast, _direct, _history, close]);
        layout.Controls.Add(actions, 0, 2);
        CancelButton = close;
        _stations.SelectionChanged += (_, _) => SetActions();
        SetActions();
    }

    public void SetStations(IReadOnlyList<HfHeardStation> stations)
    {
        string? selection = _stations.CurrentRow?.Tag as string;
        _stations.Rows.Clear();
        foreach (var station in stations)
        {
            int index = _stations.Rows.Add(station.Callsign, station.LastHeard.ToLocalTime().ToString("g"),
                HfLinkView.FormatText(station.LastFormat));
            _stations.Rows[index].Tag = station.Callsign;
            if (station.Callsign == selection) _stations.CurrentCell = _stations.Rows[index].Cells[0];
        }
        _status.Text = stations.Count == 0 ? "Stations appear here after HFLink decodes a message." :
            "Callsigns come from received messages. Last heard is the time of reception.";
        SetActions();
    }

    private void SetActions() => _broadcast.Enabled = _direct.Enabled = _history.Enabled = _stations.CurrentRow?.Tag is string;

    private Button ActionButton(string title, HfMessageFormat? format)
    {
        var button = new Button { Text = title, AutoSize = true };
        button.Click += (_, _) =>
        {
            if (_stations.CurrentRow?.Tag is not string callsign) return;
            SelectedCallsign = callsign;
            DraftFormat = format;
            DialogResult = DialogResult.OK;
        };
        return button;
    }
}
