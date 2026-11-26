using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

/// <summary>Prepares the Windows app profile before opening MeshCore Command.</summary>
internal sealed class NativeCandidateStartup : Form
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(440, 0), Text = "Preparing your MeshCore Command profile…" };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, Visible = false };
    private readonly string _registry, _oldProfile, _oldData;
    private readonly int _port;
    private readonly ICredentialStore _credentials;
    private bool _working = true;
    public NativeProfile? Profile { get; private set; }
    internal bool Working => _working;
    internal string Status => _status.Text;

    public NativeCandidateStartup(string? registry = null, string? oldProfile = null, string? oldData = null, int port = 19483, bool hidden = false, ICredentialStore? credentials = null)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _registry = registry ?? Path.Combine(local, "MeshCore Command Native Candidate Data");
        _oldProfile = oldProfile ?? Path.Combine(local, "MeshCore WiFi Data", "WebView", "EBWebView");
        _oldData = oldData ?? Path.Combine(local, "MeshCore Command Data");
        _port = port;
        _credentials = credentials ?? (hidden ? new PreviewCredentials() : new WindowsBackupCredentials());
        Text = "MeshCore Command"; Width = 510; Height = 190; StartPosition = FormStartPosition.CenterScreen;
        if (hidden) { Opacity = 0; ShowInTaskbar = false; }
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(18), WrapContents = false };
        layout.Controls.Add(_status); layout.Controls.Add(_close); Controls.Add(layout);
        _close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (_working) { e.Cancel = true; _status.Text = "Please wait until the profile is ready."; } };
        Shown += async (_, _) => await PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        try
        {
            if (!File.Exists(Path.Combine(_registry, "current.json")) && !Directory.Exists(_oldProfile) && !Directory.Exists(_oldData))
            {
                Directory.CreateDirectory(_registry);
                _ = await CredentialRecovery.CreateAsync(_registry, _credentials);
                Profile = await NativeProfileRegistry.CreateEmptyAsync(_registry, _port);
            }
            else
            {
                Profile = await NativeProfileRegistry.PrepareAsync(_registry, _oldProfile, _oldData, _port,
                    beforeCommit: async token => { _ = await CredentialRecovery.CreateAsync(_registry, _credentials, token); });
            }
            _working = false; Close();
        }
        catch (Exception error)
        {
            _working = false;
            _status.Text = "Could not prepare the MeshCore Command profile. Your existing data was not changed. " + error.Message;
            _close.Visible = true;
        }
    }

}
