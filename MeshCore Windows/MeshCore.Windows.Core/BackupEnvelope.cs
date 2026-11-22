using System.Security.Cryptography;
using System.Text;

namespace MeshCore.Windows.Core;

/// <summary>Encryption envelope shared with the existing MCCFULL1 backup format.</summary>
public static class BackupEnvelope
{
    private static readonly byte[] Magic = "MCCFULL1"u8.ToArray();
    public const int MaximumArchiveBytes = 256 * 1024 * 1024;
    private const int Iterations = 600_000;

    public static byte[] Encrypt(ReadOnlySpan<byte> archive, string password)
    {
        if (archive.Length > MaximumArchiveBytes) throw new InvalidDataException("Backup is too large.");
        ValidatePassword(password);
        byte[] result = new byte[8 + 16 + 12 + archive.Length + 16];
        Magic.CopyTo(result, 0);
        RandomNumberGenerator.Fill(result.AsSpan(8, 16));
        RandomNumberGenerator.Fill(result.AsSpan(24, 12));
        byte[] key = Derive(password, result.AsSpan(8, 16));
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(24, 12), archive, result.AsSpan(36, archive.Length), result.AsSpan(36 + archive.Length, 16), Magic);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return result;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, string password)
    {
        if (envelope.Length < 52 || envelope.Length > MaximumArchiveBytes + 1024 || !envelope[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid or unsupported full backup.");
        ValidatePassword(password);
        var plain = new byte[envelope.Length - 52];
        byte[] key = Derive(password, envelope.Slice(8, 16));
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Slice(24, 12), envelope.Slice(36, plain.Length), envelope[^16..], plain, Magic);
            return plain;
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(plain); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void ValidatePassword(string password)
    {
        // Python counts Unicode code points, rather than UTF-16 code units.
        if (password is null || password.EnumerateRunes().Count() < 12)
            throw new ArgumentException("Use a backup password of at least 12 characters.", nameof(password));
    }

    private static byte[] Derive(string password, ReadOnlySpan<byte> salt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
