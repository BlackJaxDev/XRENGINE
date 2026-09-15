using System.Security.AccessControl;
using System.Security.Principal;

namespace XREngine.ControlPlane.Service;

/// <summary>Creates a private configuration/log directory for an owned worker generation.</summary>
internal static class PrivateWorkerDirectory
{
    public static string Create(string root, Guid generation)
    {
        string directory = Path.Combine(Path.GetFullPath(root), generation.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("A worker generation directory already exists.");
        
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot identify the local worker directory owner.");
        
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner, 
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, 
            PropagationFlags.None, 
            AccessControlType.Allow));
        
        // Apply the DACL during creation. Setting owner afterward unnecessarily requires WRITE_OWNER.
        new DirectoryInfo(directory).Create(security);

        return directory;
    }
}
