using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Versioned 480-byte browser engine surface companion indexed by the canonical material's stable row ID.
/// Its six role references own resources independently, including references aliased by legacy slots.
/// Desktop canonical material layouts do not consume this companion.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct AdvancedEngineSurfaceRecord
{
    public const uint CurrentSchemaVersion = 6;
    public const int RoleCount = 6;
    public const uint WordCount = 120;
    public const uint TexturedAlphaKind = 4;
    public const uint AuthoredTexturedKind = 5;
    public const uint UnlitColorKind = 6;
    public const uint UnlitTextureKind = 7;
    public const uint UnlitOpaqueTextureKind = 8;
    public const uint UnlitAlphaTextureKind = 9;
    public const uint UnlitTextureArraySliceKind = 10;

    public uint Generation;
    public uint SchemaVersion;
    public uint SurfaceKind;
    public uint RoleFlags;
    public Vector4 BaseColorOpacity;
    public Vector4 RoughnessMetallicSpecularEmission;
    public AdvancedEngineSurfaceTextureRole BaseColor;
    public AdvancedEngineSurfaceTextureRole Normal;
    public AdvancedEngineSurfaceTextureRole Metallic;
    public AdvancedEngineSurfaceTextureRole Roughness;
    public AdvancedEngineSurfaceTextureRole Opacity;
    public AdvancedEngineSurfaceTextureRole Specular;
    /// <summary>Explicit normal-map mode, height-map scale, and reserved zeros.</summary>
    public Vector4 NormalControls;
    public uint BaseColorSampling, NormalSampling, MetallicSampling, RoughnessSampling, OpacitySampling, SpecularSampling;
    public uint SamplingReserved0, SamplingReserved1;

    public readonly uint GetSamplingKey(int index) => index switch
    {
        0 => BaseColorSampling, 1 => NormalSampling, 2 => MetallicSampling, 3 => RoughnessSampling,
        4 => OpacitySampling, 5 => SpecularSampling,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public readonly AdvancedEngineSurfaceTextureRole GetRole(int index) => index switch
    {
        0 => BaseColor,
        1 => Normal,
        2 => Metallic,
        3 => Roughness,
        4 => Opacity,
        5 => Specular,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public void SetBindings(ReadOnlySpan<AdvancedMaterialTextureBinding> bindings)
    {
        if (bindings.Length != RoleCount)
            throw new ArgumentException("An engine surface requires exactly six role bindings.", nameof(bindings));
        BaseColor.Binding = bindings[0];
        Normal.Binding = bindings[1];
        Metallic.Binding = bindings[2];
        Roughness.Binding = bindings[3];
        Opacity.Binding = bindings[4];
        Specular.Binding = bindings[5];
    }

    public readonly bool HasSameLayout(in AdvancedEngineSurfaceRecord other)
        => SchemaVersion == other.SchemaVersion && SurfaceKind == other.SurfaceKind && RoleFlags == other.RoleFlags &&
            BaseColorSampling == other.BaseColorSampling && NormalSampling == other.NormalSampling &&
            MetallicSampling == other.MetallicSampling && RoughnessSampling == other.RoughnessSampling &&
            OpacitySampling == other.OpacitySampling && SpecularSampling == other.SpecularSampling &&
            BaseColor.HasSameSamplingMetadata(in other.BaseColor) && Normal.HasSameSamplingMetadata(in other.Normal) &&
            Metallic.HasSameSamplingMetadata(in other.Metallic) && Roughness.HasSameSamplingMetadata(in other.Roughness) &&
            Opacity.HasSameSamplingMetadata(in other.Opacity) && Specular.HasSameSamplingMetadata(in other.Specular);

    public readonly bool HasSameBindings(in AdvancedEngineSurfaceRecord other)
        => BaseColor.Binding == other.BaseColor.Binding && Normal.Binding == other.Normal.Binding &&
            Metallic.Binding == other.Metallic.Binding && Roughness.Binding == other.Roughness.Binding &&
            Opacity.Binding == other.Opacity.Binding && Specular.Binding == other.Specular.Binding;
}
