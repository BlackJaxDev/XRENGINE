using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLRay
{
    public IPLVector3 origin;
    public IPLVector3 direction;
}