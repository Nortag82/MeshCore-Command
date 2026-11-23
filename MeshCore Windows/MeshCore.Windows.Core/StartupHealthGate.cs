using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace MeshCore.Windows.Core;

/// <summary>Checks a one-time startup receipt before an upgrade can be considered successful.</summary>
public static class StartupHealthGate
{
    public static async Task WriteAsync(string receiptPath, string nonce, CancellationToken cancellationToken = default)
    {
        ValidateNonce(nonce);
        string path = Path.GetFullPath(receiptPath), directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Invalid startup receipt directory.");
        string temporary = Path.Combine(directory, ".startup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(Encoding.ASCII.GetBytes(nonce), cancellationToken); file.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task WaitAsync(string receiptPath, string nonce, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateNonce(nonce);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        string path = Path.GetFullPath(receiptPath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != 64)
                        throw new InvalidDataException("Invalid startup receipt.");
                    byte[] actual = await File.ReadAllBytesAsync(path, deadline.Token);
                    byte[] expected = Encoding.ASCII.GetBytes(nonce);
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidDataException("Startup receipt does not match the update.");
                    return;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The updated app did not confirm startup; the previous installation must be retained."); }
    }

    /// <summary>Waits for a receipt while detecting an application that exits during startup.</summary>
    public static async Task WaitForProcessAsync(Process process, string receiptPath, string nonce, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task receipt = WaitAsync(receiptPath, nonce, timeout, wait.Token);
        Task exited = process.WaitForExitAsync(wait.Token);
        try
        {
            Task completed = await Task.WhenAny(receipt, exited);
            if (completed == receipt) { await receipt; return; }
            await exited;
            // Check the file again to handle the app exiting immediately after writing it.
            if (File.Exists(receiptPath)) { await receipt; return; }
            throw new IOException("The updated app exited before confirming startup; the previous installation must be retained.");
        }
        finally { await wait.CancelAsync(); }
    }

    private static void ValidateNonce(string nonce)
    { if (nonce is null || nonce.Length != 64 || nonce.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid startup nonce.", nameof(nonce)); }
}
