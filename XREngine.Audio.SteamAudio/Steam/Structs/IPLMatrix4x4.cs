using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLMatrix4x4
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public float[,] elements;
}