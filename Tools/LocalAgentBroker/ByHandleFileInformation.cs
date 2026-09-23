using System.Runtime.InteropServices;

namespace XREngine.LocalAgentBroker;

/// <summary>Native Windows file metadata used to identify hard-linked files.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}
