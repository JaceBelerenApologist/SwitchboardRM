using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SwitchboardRM;

/// <summary>
/// Saves RDP passwords in Windows Credential Manager (Control Panel > Credential Manager > Windows Credentials,
/// entries named "SwitchboardRM:&lt;user&gt;"). Windows encrypts them with your sign-in, so only you on this PC can read them.
/// </summary>
internal static class CredentialStore
{
    private const string Prefix = AppInfo.DataName + ":";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2; // this PC only; doesn't roam with your profile
    private const int ERROR_NOT_FOUND = 1168;

    /// <summary>The saved password for this RDP user, or null if none is saved.</summary>
    public static string Read(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;
        if (!CredRead(Target(user), CRED_TYPE_GENERIC, 0, out IntPtr ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    /// <summary>
    /// Copy passwords saved under an older name (e.g. "AwsRdpLauncher:user" from 1.x) to this name, leaving the
    /// originals in place. Users that already have a password here are skipped. Returns how many were copied.
    /// </summary>
    public static int CopyFromPrefix(string oldPrefix)
    {
        if (!CredEnumerate(oldPrefix + "*", 0, out int count, out IntPtr list)) return 0;
        int copied = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                string user = !string.IsNullOrEmpty(c.UserName) ? c.UserName : c.TargetName.Substring(oldPrefix.Length);
                if (string.IsNullOrWhiteSpace(user) || Exists(user)) continue;
                string password = c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
                Save(user, password);
                copied++;
            }
        }
        finally { CredFree(list); }
        return copied;
    }

    /// <summary>True if a password is saved for this user (without reading the password itself).</summary>
    public static bool Exists(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return false;
        if (!CredRead(Target(user), CRED_TYPE_GENERIC, 0, out IntPtr ptr)) return false;
        CredFree(ptr);
        return true;
    }

    /// <summary>Every RDP user that has a saved password, as typed when saved.</summary>
    public static List<string> ListUsers()
    {
        var users = new List<string>();
        if (!CredEnumerate(Prefix + "*", 0, out int count, out IntPtr list)) return users; // none saved
        try
        {
            for (int i = 0; i < count; i++)
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                users.Add(!string.IsNullOrEmpty(c.UserName) ? c.UserName : c.TargetName.Substring(Prefix.Length));
            }
        }
        finally { CredFree(list); }
        return users.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Save(string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password ?? "");
        IntPtr buf = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, buf, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(user),
                Comment = "RDP password saved by " + AppInfo.Name,
                CredentialBlobSize = blob.Length,
                CredentialBlob = buf,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = user.Trim(),
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // don't leave the password lying around in memory longer than needed
            Marshal.Copy(new byte[blob.Length], 0, buf, blob.Length);
            Marshal.FreeHGlobal(buf);
            Array.Clear(blob);
        }
    }

    public static void Delete(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return;
        if (!CredDelete(Target(user), CRED_TYPE_GENERIC, 0))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }

    private static string Target(string user) => Prefix + user.Trim().ToLowerInvariant();

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

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
