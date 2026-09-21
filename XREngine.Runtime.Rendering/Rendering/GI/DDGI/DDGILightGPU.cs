using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>std140-compatible direct-light record consumed by DDGI hit shading.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DDGILightGPU
{
    public Vector4 PositionOrDirectionAndType;
    public Vector4 ColorAndIntensity;
    public Vector4 DirectionAndInnerCutoff;
    public Vector4 RadiusOuterExponentAndFlags;
}
