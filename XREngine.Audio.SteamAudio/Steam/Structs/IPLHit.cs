using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLHit
{
    public float distance;
    public int triangleIndex;
    public int objectIndex;
    public int materialIndex;
    public IPLVector3 normal;
    public IntPtr material;
}