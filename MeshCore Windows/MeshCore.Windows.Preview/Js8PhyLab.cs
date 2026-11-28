using System.ComponentModel;
using System.Text.Json;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

internal static class Js8PhyLab
{
    internal static string? Executable()
    {
        string? path = Environment.GetEnvironmentVariable("MCC_JS8_PHY");
        return Path.IsPathFullyQualified(path ?? "") && File.Exists(path) ? path : null;
    }

    internal static void Open(Control owner, string executable, string? radioInput)
    {
        using var stop = new CancellationTokenSource();
        using var dialog = new Form { Text = "HF Lab · JS8 receiver", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(790, 425), MinimumSize = new Size(620, 310) };
        using var file = new OpenFileDialog { Filter = "WAV audio (*.wav)|*.wav", CheckFileExists = true };
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        mode.Items.AddRange(["Normal", "Slow", "Fast", "Turbo", "Ultra"]);
        mode.SelectedIndex = 0;
        var offset = new NumericUpDown { Minimum = 100, Maximum = 3500, Value = 1500, Width = 75, AccessibleName = "Preferred audio offset in hertz" };
        var decode = new HfLinkTheme.ActionButton { Text = "Decode WAV…", AutoSize = true };
        var receive = new HfLinkTheme.ActionButton { Text = "Start receive", AutoSize = true, Enabled = !string.IsNullOrWhiteSpace(radioInput) };
        var inputLevel = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        var status = new Label { Text = receive.Enabled ? "Choose file audio or start receiving from the HFLink input." :
            "Choose file audio or select a radio input in HFLink Settings.", AutoSize = true };
        var rows = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false };
        foreach (var label in new[] { "Slot UTC", "Raw frame", "Type", "Offset (Hz)", "SNR (dB)", "Timing offset (s)" })
            rows.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = label, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        controls.Controls.AddRange([mode, offset, new Label { Text = "Hz", AutoSize = true }, decode, receive, inputLevel]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(controls, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(rows, 0, 2);
        dialog.Controls.Add(layout);
        HfLinkTheme.Apply(dialog);
        int[] modes = [0, 4, 1, 2, 8];
        CancellationTokenSource? live = null;
        dialog.FormClosed += (_, _) => stop.Cancel();
        receive.Click += async (_, _) =>
        {
            if (live is not null)
            {
                receive.Enabled = false;
                live.Cancel();
                return;
            }
            using var listening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            live = listening;
            receive.Text = "Stop receive";
            decode.Enabled = mode.Enabled = offset.Enabled = false;
            rows.Rows.Clear();
            int selected = modes[mode.SelectedIndex], preferredOffset = (int)offset.Value;
            var radioStatus = new Progress<string>(message =>
            {
                if (!dialog.IsDisposed && !listening.IsCancellationRequested) status.Text = message;
            });
            var level = new Progress<double>(db =>
            {
                if (!dialog.IsDisposed && !listening.IsCancellationRequested) inputLevel.Text = $"Input {db:0} dBFS";
            });
            var results = new Progress<Js8LiveResult>(result =>
            {
                if (dialog.IsDisposed || listening.IsCancellationRequested) return;
                AddRows(result.Frames, result.Start.ToString("HH:mm:ss"));
                status.Text = $"Receiving · {result.Frames.Length} frame(s) at {result.Start:HH:mm:ss} UTC";
            });
            try
            {
                await Task.Run(() => Js8LiveReceiver.RunAsync(executable, radioInput!, selected, preferredOffset,
                    radioStatus, level, results, listening.Token));
            }
            catch (OperationCanceledException) when (listening.IsCancellationRequested)
            {
                if (!dialog.IsDisposed) status.Text = "Receive stopped.";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
                JsonException or Win32Exception or OperationCanceledException or System.Runtime.InteropServices.COMException)
            {
                if (!dialog.IsDisposed) status.Text = error.Message;
            }
            finally
            {
                live = null;
                if (!dialog.IsDisposed)
                {
                    inputLevel.Text = "";
                    receive.Text = "Start receive";
                    receive.Enabled = decode.Enabled = mode.Enabled = offset.Enabled = true;
                }
            }
        };
        decode.Click += async (_, _) =>
        {
            if (file.ShowDialog(dialog) != DialogResult.OK) return;
            int selected = modes[mode.SelectedIndex];
            decode.Enabled = false;
            receive.Enabled = false;
            rows.Rows.Clear();
            status.Text = "Decoding file…";
            try
            {
                await using var worker = await Js8PhyClient.OpenAsync(executable, stop.Token);
                var frames = await worker.DecodeWavAsync(file.FileName, selected, (int)offset.Value, stop.Token);
                if (dialog.IsDisposed) return;
                AddRows(frames, "");
                status.Text = $"{frames.Length} decoded frame(s) · JS8Call v3.0.3 PHY";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
                JsonException or Win32Exception or OperationCanceledException)
            {
                if (!dialog.IsDisposed) status.Text = error is OperationCanceledException ? "The decode timed out." : error.Message;
            }
            finally
            {
                if (!dialog.IsDisposed)
                {
                    decode.Enabled = true;
                    receive.Enabled = !string.IsNullOrWhiteSpace(radioInput);
                }
            }
        };

        void AddRows(IEnumerable<Js8PhyFrame> frames, string slot)
        {
            foreach (var frame in frames)
            {
                rows.Rows.Add(slot, frame.Frame, frame.Type, frame.OffsetHz.ToString("0.0"), frame.Snr, frame.TimeOffsetSeconds.ToString("0.000"));
                if (rows.Rows.Count > 500) rows.Rows.RemoveAt(0);
            }
        }

        dialog.ShowDialog(owner);
    }
}
