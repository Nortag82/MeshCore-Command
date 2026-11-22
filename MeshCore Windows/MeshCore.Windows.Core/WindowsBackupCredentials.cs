using System.Runtime.Versioning;

namespace MeshCore.Windows.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsBackupCredentials : ICredentialStore
{
    public string? Get(string scope, string key) => WindowsCredentials.Get(scope, key);
    public void Put(string scope, string key, string value) => WindowsCredentials.Put(scope, key, value);
    public void Delete(string scope, string key) => WindowsCredentials.Delete(scope, key);
    public IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null) => WindowsCredentials.ListSaved(scope, key);
}
