using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// 96-byte std430 surface attributes indexed by the original aggregate primitive
/// id in PackedTriangle.extra.w. BVH sorting leaves this buffer unchanged.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGITriangleAttributesGpu
{
    public Vector4 Normal0;
    public Vector4 Normal1;
    public Vector4 Normal2;
    public Vector4 Uv01_0;
    public Vector4 Uv01_1;
    public Vector4 Uv01_2;
}
