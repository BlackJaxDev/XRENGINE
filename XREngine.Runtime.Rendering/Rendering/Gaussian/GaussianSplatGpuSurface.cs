using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// Per-splat surface shading payload written by <c>GaussianLiveSurfaceSplats.comp</c> and read by
/// <c>GaussianSplatUberQuad.vs</c>. The layout is std430 and 96 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GaussianSplatGpuSurface
{
    /// <summary>Interpolated primary and secondary texture coordinates (UV0 in XY, UV1 in ZW).</summary>
    public Vector4 Uv0Uv1;

    /// <summary>Affine UV0 partial derivatives along the splat's local +X tangent axis (XY) and +Y tangent axis (ZW).</summary>
    public Vector4 DUv0LocalXY;

    /// <summary>Interpolated world-space shading normal (XYZ) and tangent handedness sign (+1 or -1 in W).</summary>
    public Vector4 WorldNormalAndSign;

    /// <summary>Interpolated world-space shading tangent (XYZ) and padding (W).</summary>
    public Vector4 WorldTangentPad;

    /// <summary>Rest-pose local position (XYZ) and padding (W).</summary>
    public Vector4 LocalPosPad;

    /// <summary>Dense material row index in bits 0..15 and face-cull mode in bits 16..17.</summary>
    public uint PackedMaterialAndFlags;

    /// <summary>UNORM4x8 RGBA vertex color interpolated at the sample point.</summary>
    public uint PackedColor0;

    /// <summary>Reserved std430 padding word 0.</summary>
    public uint Reserved0;

    /// <summary>Reserved std430 padding word 1.</summary>
    public uint Reserved1;
}
