using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BwgTopBar;

// API Key 不存入 settings.json；由 Windows Credential Manager 保护，仅当前登录用户使用。
internal static class WindowsCredentialStore
{
    private const string Target = "BwgTopBar/KiwiVmApiKey";
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2; // Windows 凭据持久化选项，不代表管理员/机器级访问。

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref Credential credential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    public static string Read()
    {
        if (!CredRead(Target, Generic, 0, out var pointer)) return "";
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(pointer);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            var bytes = new byte[checked((int)cred.CredentialBlobSize)];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string apiKey)
    {
        var data = Encoding.Unicode.GetBytes(apiKey);
        IntPtr blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, blob, data.Length);
            var cred = new Credential
            {
                Type = Generic,
                TargetName = Target,
                CredentialBlobSize = (uint)data.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            if (blob != IntPtr.Zero)
            {
                // 注意：Windows 凭据存储接收了副本后，清理临时非托管内存。
                for (var i = 0; i < data.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }
    }
}
