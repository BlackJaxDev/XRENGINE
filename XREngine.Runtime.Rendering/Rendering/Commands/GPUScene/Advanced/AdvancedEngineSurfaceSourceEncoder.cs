using System.Numerics;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Captures the exact engine surface and its independent texture roles before resource
/// acquisition. Untagged materials retain their existing canonical material contract.
/// </summary>
public static class AdvancedEngineSurfaceSourceEncoder
{
    /// <summary>
    /// Reads the shared typed surface schema without allocating a binding or resource array.
    /// The destination contains exactly four roles in base-color, normal, metallic, roughness order.
    /// </summary>
    public static bool TryEncode(
        XRMaterial? material,
        out AdvancedEngineSurfaceRecord record,
        Span<AdvancedGpuResourceBindingSource> roles,
        out string reason)
    {
        record = default;
        roles.Clear();
        reason = string.Empty;
        if (roles.Length != AdvancedEngineSurfaceRecord.RoleCount)
        {
            reason = "An engine surface source requires exactly four texture role destinations.";
            return false;
        }
        if (material is null)
            return true;

        StandardLitColorSurface values;
        MaterialSurfaceTextureBinding? baseColor = null;
        MaterialSurfaceTextureBinding? normal = null;
        MaterialSurfaceTextureBinding? metallic = null;
        MaterialSurfaceTextureBinding? roughness = null;
        uint surfaceKind;
        switch (material.EngineSemantic.Semantic)
        {
            case EngineMaterialSemantic.StandardLitColor:
                if (!StandardLitColorSurfaceBinding.TryRead(material, out values, out string? colorReason))
                {
                    reason = colorReason ?? "The engine lit-color surface failed typed schema validation.";
                    return false;
                }
                if (values.TransparencyMode != ETransparencyMode.Opaque)
                {
                    reason = "The native engine surface companion does not support masked or blended lit-color coverage.";
                    return false;
                }
                surfaceKind = 1;
                break;

            case EngineMaterialSemantic.StandardLitTexture:
                if (!StandardLitTextureSurfaceBinding.TryRead(material, out StandardLitTextureSurface textureSurface, out string? textureReason))
                {
                    reason = textureReason ?? "The engine lit-texture surface failed typed schema validation.";
                    return false;
                }
                values = textureSurface.Values;
                baseColor = textureSurface.BaseColor;
                normal = textureSurface.Normal;
                metallic = textureSurface.Metallic;
                roughness = textureSurface.Roughness;
                surfaceKind = normal is null ? 2u : 3u;
                break;

            case EngineMaterialSemantic.AuthoredLit:
                reason = "Authored lit materials require proven engine-generated native surface provenance; a cooked raster shader identity does not establish native shading equivalence.";
                return false;

            default:
                return true;
        }

        if (!float.IsFinite(values.BaseColor.X) || !float.IsFinite(values.BaseColor.Y) ||
            !float.IsFinite(values.BaseColor.Z) || !float.IsFinite(values.Opacity))
        {
            reason = "The native engine surface BaseColorOpacity requires finite base-color and opacity values.";
            return false;
        }
        if (!float.IsFinite(values.Roughness) || !float.IsFinite(values.Metallic) ||
            !float.IsFinite(values.Specular) || !float.IsFinite(values.Emission))
        {
            reason = "The native engine surface RoughnessMetallicSpecularEmission requires finite roughness, metallic, specular, and emission values.";
            return false;
        }

        if (!AdvancedGpuResourceSourceEncoder.TryEncode(baseColor?.Texture, EAdvancedResourceFallback.White, out roles[0], out _, out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(normal?.Texture, EAdvancedResourceFallback.FlatNormal, out roles[1], out _, out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(metallic?.Texture, EAdvancedResourceFallback.White, out roles[2], out _, out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(roughness?.Texture, EAdvancedResourceFallback.White, out roles[3], out _, out reason))
        {
            roles.Clear();
            return false;
        }

        record = new AdvancedEngineSurfaceRecord
        {
            SchemaVersion = AdvancedEngineSurfaceRecord.CurrentSchemaVersion,
            SurfaceKind = surfaceKind,
            RoleFlags = (baseColor is null ? 0u : 1u) | (normal is null ? 0u : 2u) |
                (metallic is null ? 0u : 4u) | (roughness is null ? 0u : 8u),
            BaseColorOpacity = new Vector4(values.BaseColor, values.Opacity),
            RoughnessMetallicSpecularEmission = new Vector4(values.Roughness, values.Metallic, values.Specular, values.Emission),
            BaseColor = CreateRole(baseColor, false),
            Normal = CreateRole(normal, true),
            Metallic = CreateRole(metallic, false),
            Roughness = CreateRole(roughness, false),
        };
        return true;
    }

    private static AdvancedEngineSurfaceTextureRole CreateRole(MaterialSurfaceTextureBinding? source, bool isNormal)
        => source is null
            ? new AdvancedEngineSurfaceTextureRole { UvScaleOffset = new Vector4(1, 1, 0, 0) }
            : new AdvancedEngineSurfaceTextureRole
            {
                UvScaleOffset = source.UvScaleOffset,
                UvRotation = source.UvRotation,
                TexCoordSet = checked((uint)source.TexCoordSet),
                Channel = checked((uint)source.Channel),
                DecodeFlags = (source.IsSrgb ? 1u : 0u) | (isNormal ? 2u : 0u),
            };
}
