namespace MeshCore.Windows.Preview;

/// <summary>Shows one synthetic notification without connecting a radio or loading user data.</summary>
internal sealed class NotificationTest : Form
{
    private readonly NotifyIcon _tray = new();
    private readonly DesktopNotifications _notifications;
    public NotificationTest()
    {
        _notifications = new DesktopNotifications(this, () => { });
        Opacity = 0; ShowInTaskbar = false;
        Shown += async (_, _) => {
            try
            {
                _tray.Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "meshcore-command.ico"));
                _tray.Text = "MeshCore Command notification test"; _tray.Visible = true;
                await Task.Delay(1200);
                _notifications.Show("MeshCore Command test", "This is a test notification; no message was sent.");
                await Task.Delay(15000);
            }
            catch { Environment.ExitCode = 1; }
            finally { _notifications.Dispose(); _tray.Visible = false; _tray.Dispose(); Close(); }
        };
    }
}
