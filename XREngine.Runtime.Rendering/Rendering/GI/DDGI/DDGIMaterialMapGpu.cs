using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>48-byte std430 texture reference, UV transform, and per-binding sampling policy.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIMaterialMapGpu
{
    public Vector4 Source; // layer (-1 absent), UV set, scalar channel, manual sRGB decode
    public Vector4 UvScaleOffset;
    public Vector4 WrapRotation; // wrap U, wrap V, sin(rotation), cos(rotation)

    public static DDGIMaterialMapGpu None => new()
    {
        Source = new Vector4(-1, 0, 0, 0),
        UvScaleOffset = new Vector4(1, 1, 0, 0),
        WrapRotation = new Vector4(0, 0, 0, 1),
    };
}
