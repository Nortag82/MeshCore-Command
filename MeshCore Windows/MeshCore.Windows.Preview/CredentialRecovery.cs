using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Preview;

/// <summary>Same-Windows-user recovery for credentials before an experimental migration.</summary>
internal static class CredentialRecovery
{
    private static readonly byte[] Magic = "MCCRED1"u8.ToArray();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeshCore Command credential recovery v1");
    private const int Limit = 16 * 1024 * 1024;

    public static async Task<string> CreateAsync(string registryDirectory, ICredentialStore credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var values = credentials.ListSaved();
        byte[] validated = BackupArchive.Create(new Dictionary<string, string>(), values, new Dictionary<string, byte[]>());
        CryptographicOperations.ZeroMemory(validated);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(values);
        if (plain.Length > Limit) { CryptographicOperations.ZeroMemory(plain); throw new InvalidDataException("Credential recovery data is too large."); }
        byte[] protectedData;
        try { protectedData = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string root = Path.GetFullPath(registryDirectory), folder = Path.Combine(root, "recovery");
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery directory cannot be a link.");
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery directory cannot be a link.");
        string path = Path.Combine(folder, "credentials-" + Guid.NewGuid().ToString("N") + ".mcccred");
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await file.WriteAsync(Magic, cancellationToken); await file.WriteAsync(protectedData, cancellationToken); file.Flush(true);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        finally { CryptographicOperations.ZeroMemory(protectedData); }
        return path;
    }

    public static void Restore(string path, IBackupCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 8 or > Limit + 4096) throw new InvalidDataException("Invalid credential recovery file.");
        byte[] data = File.ReadAllBytes(path);
        if (!data.AsSpan().StartsWith(Magic)) throw new InvalidDataException("Invalid credential recovery file.");
        byte[] plain = ProtectedData.Unprotect(data.AsSpan(Magic.Length).ToArray(), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? throw new InvalidDataException("Invalid credential recovery data.");
            // Reuse the full-backup validation and transactional rollback rules.
            byte[] archive = BackupArchive.Create(new Dictionary<string, string>(), values, new Dictionary<string, byte[]>());
            try { BackupRestore.Apply(archive, Path.GetTempPath(), credentials); }
            finally { CryptographicOperations.ZeroMemory(archive); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(data); }
    }
}
