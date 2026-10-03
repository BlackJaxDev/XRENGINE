using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Versioned 304-byte engine surface companion indexed by the canonical material's stable row ID.
/// Its four role references own resources independently, including references aliased by legacy slots.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct AdvancedEngineSurfaceRecord
{
    public const uint CurrentSchemaVersion = 1;
    public const int RoleCount = 4;
    public const uint WordCount = 76;

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

    public readonly AdvancedEngineSurfaceTextureRole GetRole(int index) => index switch
    {
        0 => BaseColor,
        1 => Normal,
        2 => Metallic,
        3 => Roughness,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public void SetBindings(ReadOnlySpan<AdvancedMaterialTextureBinding> bindings)
    {
        if (bindings.Length != RoleCount)
            throw new ArgumentException("An engine surface requires exactly four role bindings.", nameof(bindings));
        BaseColor.Binding = bindings[0];
        Normal.Binding = bindings[1];
        Metallic.Binding = bindings[2];
        Roughness.Binding = bindings[3];
    }

    public readonly bool HasSameLayout(in AdvancedEngineSurfaceRecord other)
        => SchemaVersion == other.SchemaVersion && SurfaceKind == other.SurfaceKind && RoleFlags == other.RoleFlags &&
            BaseColor.HasSameSamplingMetadata(in other.BaseColor) && Normal.HasSameSamplingMetadata(in other.Normal) &&
            Metallic.HasSameSamplingMetadata(in other.Metallic) && Roughness.HasSameSamplingMetadata(in other.Roughness);

    public readonly bool HasSameBindings(in AdvancedEngineSurfaceRecord other)
        => BaseColor.Binding == other.BaseColor.Binding && Normal.Binding == other.Normal.Binding &&
            Metallic.Binding == other.Metallic.Binding && Roughness.Binding == other.Roughness.Binding;
}
