using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XREngine.UnityScriptHost.Admission;

/// <summary>Applies Unity source path rules and resolves existing directory identities.</summary>
internal static class UnitySourcePath
{
    /// <summary>Excludes hidden and tilde-suffixed directories from Unity script metadata and compilation.</summary>
    public static bool IsIgnoredCompilationPath(string path)
    {
        string[] parts = path.Split('/');
        for (int index = 1; index < parts.Length - 1; index++)
            if (parts[index].StartsWith('.') || parts[index].EndsWith('~'))
                return true;
        return false;
    }

    public static string GetDirectoryIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(fullPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The source and output directories must exist.");
        if (!OperatingSystem.IsWindows())
            return Path.TrimEndingDirectorySeparator(fullPath);

        // A handle resolves DOS, UNC, extended, and short-name aliases before containment checks.
        const uint shareReadWriteDelete = 7;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint volumeNameNt = 2;
        using SafeFileHandle handle = CreateFileW(fullPath, 0, shareReadWriteDelete, IntPtr.Zero,
            openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("Cannot resolve the directory identity.", new Win32Exception(Marshal.GetLastWin32Error()));

        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, volumeNameNt);
        if (count == 0 || count >= buffer.Capacity)
            throw new IOException("Cannot resolve the directory identity within the path limit.", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString().TrimEnd('\\');
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder filePath, uint pathLength, uint flags);
}
