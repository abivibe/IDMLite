using System.Security.AccessControl;
using System.Security.Principal;

namespace IDMLite.Core;

public static class PrivateStorage
{
    public static string UserId => OperatingSystem.IsWindows()
        ? WindowsIdentity.GetCurrent().User!.Value : Environment.UserName;

    public static void EnsureDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        string fullPath = Path.GetFullPath(path);
        for (var parent = new DirectoryInfo(fullPath); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Recovery storage cannot use junctions or symbolic links.");
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists) directory.Create(security);
        else
        {
            if (!user.Equals(directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
                throw new IOException("Recovery storage must belong to the current Windows user.");
            directory.SetAccessControl(security);
        }
    }
}
