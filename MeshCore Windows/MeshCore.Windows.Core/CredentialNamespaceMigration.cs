namespace MeshCore.Windows.Core;

/// <summary>Copies validated credentials into an independent store without changing the source.</summary>
public static class CredentialNamespaceMigration
{
    // The caller must hold the destination profile's exclusive lease throughout this operation.
    public static void Copy(ICredentialStore source, ICredentialStore destination)
    {
        var pending = Pending(source, destination);
        var written = new List<(string Scope, string Key)>();
        try
        {
            foreach (var entry in pending)
            {
                written.Add((entry.Scope, entry.Key));
                destination.Put(entry.Scope, entry.Key, entry.Value);
                if (destination.Get(entry.Scope, entry.Key) != entry.Value) throw new IOException("Credential verification failed.");
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var item in written.AsEnumerable().Reverse())
                try { destination.Delete(item.Scope, item.Key); } catch (Exception rollback) { failures.Add(rollback); }
            if (failures.Count != 0) throw new AggregateException("Credential copy failed and rollback was incomplete.", new[] { error }.Concat(failures));
            throw;
        }
    }

    /// <summary>Checks for conflicting destination credentials without writing either store.</summary>
    public static void EnsureNoConflicts(ICredentialStore source, ICredentialStore destination) => _ = Pending(source, destination);

    private static List<(string Scope, string Key, string Value)> Pending(ICredentialStore source, ICredentialStore destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(source, destination)) throw new ArgumentException("Credential stores must be separate.");
        var snapshot = source.ListSaved().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        // Validate every name and value using the same rules as full backups before writing anything.
        byte[] validation = BackupArchive.Create(new Dictionary<string, string>(), snapshot, new Dictionary<string, byte[]>());
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(validation);
        var entries = snapshot.Select(item => {
            string[] parts = item.Key.Split('/');
            return (Scope: parts[0], Key: parts[1], Value: item.Value);
        }).ToArray();
        var pending = new List<(string Scope, string Key, string Value)>();
        foreach (var entry in entries)
        {
            string? existing = destination.Get(entry.Scope, entry.Key);
            if (existing is null) pending.Add(entry);
            else if (existing != entry.Value) throw new IOException("A different destination credential already exists; no credentials were changed.");
        }
        return pending;
    }
}
