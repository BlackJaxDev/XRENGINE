using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>GPU representation of shaded probe ray radiance and hit distance in DDGIRayRadianceBuffer SSBO (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGIRayRadianceGPU
{
    /// <summary>Shaded radiance color (xyz) and ray hit distance t (w).</summary>
    public Vector4 RadianceDistance;
}
