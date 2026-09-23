using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XREngine.LocalAgentBroker;

/// <summary>Rejects Windows hard links before an in-place source-file mutation.</summary>
internal static class SwarmFileLinkInspector
{
    /// <summary>Checks the opened object, rather than only a raceable pathname, before writing.</summary>
    public static void ValidateOpenedFile(SafeFileHandle handle, string expectedFullPath, string path)
    {
        var buffer = new StringBuilder(32_768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException($"Could not resolve the opened file '{path}'.");
        string finalPath = buffer.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            finalPath = finalPath[4..];
        if (!string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(expectedFullPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The opened file '{path}' no longer resolves to its authorized path.");
        RejectHardLink(handle, path);
    }

    public static void RejectHardLink(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            throw new IOException($"Could not inspect link count for '{path}'.");
        if (information.NumberOfLinks > 1)
            throw new IOException($"Repository file '{path}' is a hard link and cannot be patched.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
