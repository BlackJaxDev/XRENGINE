using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Stable decal transform and material reference.
/// </summary>
[XREngine.Rendering.Shaders.GpuRecord("XRAdvancedDecalRecord")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDecalRecord
{
    public const uint EnabledFlag = 1u << 0;
    public const uint AffectNormalsFlag = 1u << 1;
    public const uint AuthoredAlbedoFlag = 1u << 2;
    public const uint UnsupportedAuthoredFlag = 1u << 3;
    public const uint ForwardOitFlag = 1u << 4;
    public const uint UnsupportedDrawCommandFlag = 1u << 5;

    public AdvancedGpuHandle Identity;
    public AdvancedGpuHandle Material;

    /// <summary>For the authored-albedo contract the otherwise unused material words retain the source command key, with generation zero. They are never a material handle.</summary>
    public readonly uint AuthoredCommandKey => (Flags & (AuthoredAlbedoFlag | UnsupportedAuthoredFlag)) != 0 ? Material.Index : 0u;

    /// <summary>Bit 0 enables a decal and bit 1 enables generic normal modification. Bit 2 selects the exact authored XZ albedo-only contract; otherwise rows retain their generic XY material semantics. Bit 3 records unsupported authored behavior and bit 4 identifies forward OIT.</summary>
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
