namespace MeshCore.Windows.Core;

/// <summary>Exclusive process lifetime lock for a native profile; it never stops another process.</summary>
public static class NativeInstanceLease
{
    public static FileStream Acquire(string registryDirectory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registryDirectory));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid native profile directory.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("The native profile directory cannot be a link.");
        string path = Path.Combine(root, ".instance.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The native instance lock cannot be a link.");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static async Task<FileStream> WaitAsync(string registryDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            if (deadline.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); throw new TimeoutException("Another native app is still using this profile."); }
            try { return Acquire(registryDirectory); }
            catch (IOException)
            {
                if (deadline.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); throw new TimeoutException("Another native app is still using this profile."); }
                try { await Task.Delay(200, deadline.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("Another native app is still using this profile."); }
            }
        }
    }
}
