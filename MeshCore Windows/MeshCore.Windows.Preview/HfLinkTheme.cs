namespace MeshCore.Windows.Preview;

internal static class HfLinkTheme
{
    private static readonly Color Background = Color.FromArgb(11, 18, 24);
    private static readonly Color Surface = Color.FromArgb(20, 35, 46);
    private static readonly Color Border = Color.FromArgb(53, 77, 91);
    private static readonly Color Foreground = Color.FromArgb(232, 242, 250);
    private static readonly Color Muted = Color.FromArgb(152, 170, 184);
    private static readonly Color Selected = Color.FromArgb(26, 75, 63);
    private static readonly Color Accent = Color.FromArgb(56, 225, 141);

    internal static void Apply(Control control)
    {
        if (SystemInformation.HighContrast) return;
        control.BackColor = Background;
        control.ForeColor = Foreground;
        switch (control)
        {
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = Surface;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = Selected;
                break;
            case TextBoxBase or NumericUpDown or ComboBox:
                control.BackColor = Surface;
                break;
            case DataGridView grid:
                grid.BackgroundColor = Background;
                grid.GridColor = Border;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = Foreground;
                grid.DefaultCellStyle.BackColor = Background;
                grid.DefaultCellStyle.ForeColor = Foreground;
                grid.DefaultCellStyle.SelectionBackColor = Selected;
                grid.DefaultCellStyle.SelectionForeColor = Foreground;
                break;
            case TabControl tabs:
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabs.DrawItem += DrawTab;
                break;
            case TabPage page:
                page.UseVisualStyleBackColor = false;
                break;
        }
        foreach (Control child in control.Controls) Apply(child);
    }

    private static void DrawTab(object? sender, DrawItemEventArgs args)
    {
        if (sender is not TabControl tabs) return;
        bool active = tabs.SelectedIndex == args.Index;
        using var fill = new SolidBrush(active ? Selected : Surface);
        args.Graphics.FillRectangle(fill, args.Bounds);
        TextRenderer.DrawText(args.Graphics, tabs.TabPages[args.Index].Text, tabs.Font, args.Bounds,
            active ? Accent : Foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    internal sealed class ActionButton : Button
    {
        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (Enabled || SystemInformation.HighContrast) return;
            using var fill = new SolidBrush(BackColor);
            args.Graphics.FillRectangle(fill, Rectangle.Inflate(ClientRectangle, -1, -1));
            TextRenderer.DrawText(args.Graphics, Text, Font, ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
