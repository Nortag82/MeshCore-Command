using System.Runtime.Versioning;

namespace MeshCore.Windows.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsBackupCredentials : ICredentialStore
{
    private readonly bool _native;
    public WindowsBackupCredentials(bool native = false) => _native = native;
    public string? Get(string scope, string key) => WindowsCredentials.Get(scope, key, _native);
    public void Put(string scope, string key, string value) => WindowsCredentials.Put(scope, key, value, _native);
    public void Delete(string scope, string key) => WindowsCredentials.Delete(scope, key, _native);
    public IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null) => WindowsCredentials.ListSaved(scope, key, _native);
}
