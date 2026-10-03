using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLTriangle
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public int[] indices;
}