using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniDrop.Windows.Infrastructure;

/// <summary>Windows Credential Manager 存取应用密码（§13：不写日志、不落明文文件）。</summary>
public static class CredentialManager
{
    private const string TargetName = "MiniDrop/WebDAV";

    public static void Save(string password)
    {
        Delete();
        if (password.Length == 0)
            return;
        var blob = Encoding.Unicode.GetBytes(password);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = TargetName,
            CredentialBlobSize = blob.Length,
            CredentialBlob = Marshal.AllocHGlobal(blob.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = "MiniDrop",
        };
        try
        {
            Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存凭据");
        }
        finally
        {
            Marshal.FreeHGlobal(cred.CredentialBlob);
        }
    }

    public static string? Load()
    {
        var outPtr = IntPtr.Zero;
        try
        {
            if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out outPtr))
                return null;
            var cred = Marshal.PtrToStructure<CREDENTIAL>(outPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return Encoding.Unicode.GetString(blob);
        }
        finally
        {
            if (outPtr != IntPtr.Zero)
                CredFree(outPtr);
        }
    }

    public static void Delete() => CredDelete(TargetName, CRED_TYPE_GENERIC, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);
}
