using System.Runtime.InteropServices;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents the standard startup information for a worker process, corresponding to the Win32 STARTUPINFO structure, which is embedded in STARTUPINFOEX.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorkerProcessStartup
{
    public uint Size;
    public nint Reserved, Desktop, Title;
    public uint X, Y, Width, Height, CharactersX, CharactersY, FillAttribute, Flags;
    public ushort ShowWindow, ReservedBytes;
    public nint ReservedData, StandardInput, StandardOutput, StandardError;
}
