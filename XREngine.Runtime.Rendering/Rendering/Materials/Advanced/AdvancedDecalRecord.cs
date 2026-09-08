using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Stable decal transform and material reference.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDecalRecord
{
    public const uint EnabledFlag = 1u << 0;
    public const uint AffectNormalsFlag = 1u << 1;

    public AdvancedGpuHandle Identity;
    public AdvancedGpuHandle Material;

    /// <summary>Bit 0 enables a decal. Enabled decals use standard-material base alpha as opacity, RMSE RG as roughness/metallic targets, and mask alpha as coverage; bit 1 enables normal-map modification.</summary>
    public uint Flags;
    public uint ViewMaskLo;
    public uint ViewMaskHi;
    /// <summary>Target draw-layer bits. A zero value targets every layer, preserving zero-initialized authoring records.</summary>
    public uint LayerMask;

    public Matrix4x4 WorldToDecal;
    public Matrix4x4 PreviousWorldToDecal;
    public Vector4 HalfExtentsAndFade;
    public AdvancedTextureReference MaskTexture;
}
