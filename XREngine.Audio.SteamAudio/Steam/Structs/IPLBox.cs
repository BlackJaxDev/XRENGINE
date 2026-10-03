using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLBox
{
    public IPLVector3 minCoordinates;
    public IPLVector3 maxCoordinates;
}