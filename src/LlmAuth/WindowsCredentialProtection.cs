using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace LlmAuth;

/// <summary>
/// Windows-only at-rest hardening for the credential store: DPAPI-wraps the AES key so a
/// stolen key.bin is useless to another user, and ACL-restricts credential files to the
/// current user. All methods are safe no-ops (or identity) on non-Windows.
/// </summary>
internal static class WindowsCredentialProtection
{
    // Ties the wrapped key to this store so a blob copied elsewhere still needs the user.
    private static readonly byte[] Entropy = "coda.credential-store.v1"u8.ToArray();

    public static byte[] ProtectKey(byte[] key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return key;
        }

        return ProtectWindows(key);
    }

    public static byte[] UnprotectKey(byte[] blob)
    {
        if (!OperatingSystem.IsWindows())
        {
            return blob;
        }

        return UnprotectWindows(blob);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] key) =>
        ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] blob) =>
        ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);

    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RestrictWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
        {
            return;
        }

        var isDir = Directory.Exists(path);

        if (isDir)
        {
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(sec);
        }
        else
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(sec);
        }
    }
}
