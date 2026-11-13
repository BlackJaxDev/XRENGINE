using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// Static surface-sample descriptor bound to one triangle of an avatar submesh. Each frame,
/// <c>GaussianLiveSurfaceSplats.comp</c> reads the live deformed vertices of this triangle and emits one
/// <see cref="GaussianSplatGpuSplat"/> and one <see cref="GaussianSplatGpuSurface"/> at <see cref="SplatSlot"/>.
/// The layout is std430 and 112 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GaussianLiveSurfaceGpuSample
{
    /// <summary>First vertex index of the source triangle.</summary>
    public uint Index0;

    /// <summary>Second vertex index of the source triangle.</summary>
    public uint Index1;

    /// <summary>Third vertex index of the source triangle.</summary>
    public uint Index2;

    /// <summary>Destination index in the shared splat and surface storage buffers.</summary>
    public uint SplatSlot;

    /// <summary>Barycentric weights for vertices 1 and 2 (XY), inverse square root of rest double-area (Z), and base opacity (W).</summary>
    public Vector4 BaryAndArea;

    /// <summary>Dense material index in bits 0..15 and face-cull mode (0 = None, 1 = Front, 2 = Back) in bits 16..17.</summary>
    public uint PackedMaterialAndFlags;

    /// <summary>UNORM4x8 RGBA vertex color interpolated at the sample point.</summary>
    public uint PackedColor0;

    /// <summary>IEEE-754 float bits of the rest-pose tangent standard deviation along the local X axis.</summary>
    public uint RestSigmaXBits;

    /// <summary>IEEE-754 float bits of the rest-pose tangent standard deviation along the local Y axis.</summary>
    public uint RestSigmaYBits;

    /// <summary>Interpolated UV0 (XY) and UV1 (ZW) coordinates at the sample point.</summary>
    public Vector4 Uv0Uv1;

    /// <summary>Interpolated UV2 (XY) and UV3 (ZW) coordinates at the sample point.</summary>
    public Vector4 Uv2Uv3;

    /// <summary>Rest-pose local position (XYZ) and tangent handedness sign (+1 or -1 in W).</summary>
    public Vector4 LocalPosAndSign;

    /// <summary>Triangle UV0 edge deltas: <c>uv0_1 - uv0_0</c> in XY and <c>uv0_2 - uv0_0</c> in ZW.</summary>
    public Vector4 Uv0Edges;

    /// <summary>Creates one GPU surface sample record.</summary>
    public GaussianLiveSurfaceGpuSample(
        uint index0,
        uint index1,
        uint index2,
        uint splatSlot,
        float bary1,
        float bary2,
        float invSqrtRestDoubleArea,
        float baseOpacity,
        uint packedMaterialAndFlags,
        uint packedColor0,
        float restSigmaX,
        float restSigmaY,
        Vector4 uv0Uv1,
        Vector4 uv2Uv3,
        Vector3 localPosition,
        float tangentSign,
        Vector4 uv0Edges)
    {
        Index0 = index0;
        Index1 = index1;
        Index2 = index2;
        SplatSlot = splatSlot;
        BaryAndArea = new Vector4(bary1, bary2, invSqrtRestDoubleArea, baseOpacity);
        PackedMaterialAndFlags = packedMaterialAndFlags;
        PackedColor0 = packedColor0;
        RestSigmaXBits = BitConverter.SingleToUInt32Bits(restSigmaX);
        RestSigmaYBits = BitConverter.SingleToUInt32Bits(restSigmaY);
        Uv0Uv1 = uv0Uv1;
        Uv2Uv3 = uv2Uv3;
        LocalPosAndSign = new Vector4(localPosition, tangentSign);
        Uv0Edges = uv0Edges;
    }
}
