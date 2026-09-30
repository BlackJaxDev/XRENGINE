using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLCoordinateSpace3
{
    public IPLVector3 right;
    public IPLVector3 up;
    public IPLVector3 ahead;
    public IPLVector3 origin;
}