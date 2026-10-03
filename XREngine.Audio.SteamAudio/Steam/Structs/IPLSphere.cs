using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLSphere
{
    public IPLVector3 center;
    public float radius;
}