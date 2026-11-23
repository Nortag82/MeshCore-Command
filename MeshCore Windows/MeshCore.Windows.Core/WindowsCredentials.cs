using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>Compatible with the existing app's generic Windows credentials.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsCredentials
{
    private const int Generic = 1, NotFound = 1168, LocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredEnumerateW")]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    private static string Prefix(bool native) => native ? "MeshCore Command Native" : "MeshCore Command";
    private static string Target(string scope, string key, bool native = false)
    {
        if (scope is not ("secure" or "repeater") || key is null || !Regex.IsMatch(key, "^[0-9a-f]{64}$"))
            throw new ArgumentException("Invalid credential scope or key.");
        return $"{Prefix(native)}/{scope}/{key}";
    }

    public static string? Get(string scope, string key, bool native = false)
    {
        if (!CredRead(Target(scope, key, native), Generic, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    /// <summary>Checks whether a credential exists without reading its secret value.</summary>
    public static bool Exists(string scope, string key)
    {
        if (!CredRead(Target(scope, key), Generic, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return false;
            throw new Win32Exception(error);
        }
        CredFree(pointer);
        return true;
    }

    public static void Put(string scope, string key, string value, bool native = false)
    {
        string target = Target(scope, key, native);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(value));
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = Generic, TargetName = target, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob, Persist = LocalMachine, UserName = "MeshCore repeater" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete(string scope, string key, bool native = false)
    {
        if (CredDelete(Target(scope, key, native), Generic, 0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != NotFound) throw new Win32Exception(error);
    }

    public static IReadOnlyDictionary<string, string> ListSaved(string? scope = null, string? key = null, bool native = false)
    {
        if ((scope is null) != (key is null)) throw new ArgumentException("Supply both credential scope and key, or neither.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string prefix = Prefix(native);
        string filter = scope is null && key is null ? prefix + "/*" : Target(scope!, key!, native) + "*";
        if (!CredEnumerate(filter, 0, out int count, out IntPtr pointers))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return result;
            throw new Win32Exception(error);
        }
        try
        {
            for (int i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(pointers, i * IntPtr.Size);
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var match = Regex.Match(credential.TargetName ?? "", "^" + Regex.Escape(prefix) + "/(secure|repeater)/([0-9a-f]{64})$");
                if (!match.Success || credential.Type != Generic) continue;
                byte[] bytes = new byte[checked((int)credential.CredentialBlobSize)];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                try { result.Add(match.Groups[1].Value + "/" + match.Groups[2].Value, new UTF8Encoding(false, true).GetString(bytes)); }
                finally { Array.Clear(bytes); }
            }
            return result;
        }
        finally { CredFree(pointers); }
    }
}
