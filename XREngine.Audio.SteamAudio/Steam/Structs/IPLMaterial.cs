using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLMaterial
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] absorption;
    public float scattering;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] transmission;
}