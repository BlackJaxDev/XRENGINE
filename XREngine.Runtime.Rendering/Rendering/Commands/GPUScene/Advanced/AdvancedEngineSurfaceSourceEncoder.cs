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
    /// The destination contains base-color, normal, metallic, roughness, opacity, and specular roles in that order.
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
            reason = "An engine surface source requires exactly six texture role destinations.";
            return false;
        }
        if (material is null)
            return true;

        StandardLitColorSurface values;
        MaterialSurfaceTextureBinding? baseColor = null;
        MaterialSurfaceTextureBinding? normal = null;
        MaterialSurfaceTextureBinding? metallic = null;
        MaterialSurfaceTextureBinding? roughness = null;
        MaterialSurfaceTextureBinding? opacity = null;
        MaterialSurfaceTextureBinding? specular = null;
        Vector4 normalControls = default;
        uint surfaceKind;
        switch (material.EngineSemantic.Semantic)
        {
            case EngineMaterialSemantic.Unlit:
                if (!EngineUnlitNativeAdmission.TryRead(material, out EngineUnlitSurface unlit, out reason))
                    return false;
                if (unlit.Semantic != material.EngineSemantic)
                {
                    reason = "The retained unlit surface identity differs from its exact target companion.";
                    return false;
                }
                if (unlit.Semantic == EngineMaterialSemanticIdentity.UnlitColorV1)
                {
                    surfaceKind = AdvancedEngineSurfaceRecord.UnlitColorKind;
                }
                else
                {
                    if (unlit.Texture is null)
                    {
                        reason = "The native unlit texture surface requires its Texture0 role.";
                        return false;
                    }
                    bool array = unlit.Semantic == EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5;
                    if (array ? unlit.Texture is not XRTexture2DArray : unlit.Texture is not XRTexture2D)
                    {
                        reason = "The native unlit Texture0 dimension differs from its semantic.";
                        return false;
                    }
                    baseColor = unlit.TextureBinding;
                    if (baseColor is null)
                    {
                        reason = "The admitted native unlit Texture0 has no retained base-color role.";
                        return false;
                    }
                    if (!ReferenceEquals(baseColor.Texture, unlit.Texture))
                    {
                        reason = "The native unlit base-color role differs from Texture0.";
                        return false;
                    }
                    surfaceKind = unlit.Semantic == EngineMaterialSemanticIdentity.UnlitTextureV2
                        ? AdvancedEngineSurfaceRecord.UnlitTextureKind
                        : unlit.Semantic == EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3
                            ? AdvancedEngineSurfaceRecord.UnlitOpaqueTextureKind
                            : unlit.Semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4
                                ? AdvancedEngineSurfaceRecord.UnlitAlphaTextureKind
                                : AdvancedEngineSurfaceRecord.UnlitTextureArraySliceKind;
                }
                if (unlit.TransparencyMode != (surfaceKind == AdvancedEngineSurfaceRecord.UnlitAlphaTextureKind
                    ? ETransparencyMode.Masked : ETransparencyMode.Opaque))
                {
                    reason = "Sorted unlit transparency requires its late raster pass and cannot enter native opaque shading.";
                    return false;
                }
                values = new StandardLitColorSurface(default, new Vector3(unlit.Color.X, unlit.Color.Y, unlit.Color.Z),
                    unlit.Color.W, 0,
                    surfaceKind == AdvancedEngineSurfaceRecord.UnlitAlphaTextureKind ? unlit.AlphaCutoff : 0,
                    0, 0, 1, unlit.TransparencyMode, unlit.AlphaCutoff);
                break;

            case EngineMaterialSemantic.StandardLitColor:
                if (!StandardLitColorSurfaceBinding.TryRead(material, out values, out string? colorReason))
                {
                    reason = colorReason ?? "The engine lit-color surface failed typed schema validation.";
                    return false;
                }
                if (values.TransparencyMode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
                {
                    reason = "Sorted lit-color transparency requires its late raster pass and cannot enter native opaque shading.";
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

            case EngineMaterialSemantic.AuthoredLitTextureAlpha:
                if (!EngineAuthoredLitNativeAdmission.TryReadTexturedAlpha(material, out TexturedAlphaSurface alphaSurface, out reason))
                    return false;
                values = alphaSurface.Values;
                if (values.TransparencyMode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
                {
                    reason = "Sorted textured-alpha transparency requires its late raster pass and cannot enter native opaque shading.";
                    return false;
                }
                baseColor = alphaSurface.BaseColor;
                opacity = alphaSurface.Opacity;
                surfaceKind = AdvancedEngineSurfaceRecord.TexturedAlphaKind;
                break;

            case EngineMaterialSemantic.AuthoredLitTextured:
                if (!EngineAuthoredLitNativeAdmission.TryReadAuthoredTextured(material, out AuthoredTexturedSurface authoredSurface, out reason))
                    return false;
                values = authoredSurface.Values;
                if (values.TransparencyMode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
                {
                    reason = "Sorted authored textured transparency requires its late raster pass and cannot enter native opaque shading.";
                    return false;
                }
                baseColor = authoredSurface.BaseColor;
                normal = authoredSurface.Normal;
                specular = authoredSurface.Specular;
                opacity = authoredSurface.Opacity;
                normalControls = normal is null ? default : new(authoredSurface.NormalMapMode, authoredSurface.HeightMapScale, 0, 0);
                surfaceKind = AdvancedEngineSurfaceRecord.AuthoredTexturedKind;
                break;

            case EngineMaterialSemantic.AuthoredLit:
                if (!EngineAuthoredLitNativeAdmission.TryRead(material, out values,
                    out StandardLitTextureSurface authoredTexture, out bool textured, out reason))
                    return false;
                if (values.TransparencyMode is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
                {
                    reason = "Sorted authored transparency requires its late raster pass and cannot enter native opaque shading.";
                    return false;
                }
                if (textured)
                {
                    baseColor = authoredTexture.BaseColor;
                    normal = authoredTexture.Normal;
                    metallic = authoredTexture.Metallic;
                    roughness = authoredTexture.Roughness;
                }
                surfaceKind = textured ? normal is null ? 2u : 3u : 1u;
                break;

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
            !AdvancedGpuResourceSourceEncoder.TryEncode(roughness?.Texture, EAdvancedResourceFallback.White, out roles[3], out _, out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(opacity?.Texture, EAdvancedResourceFallback.White, out roles[4], out _, out reason) ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(specular?.Texture, EAdvancedResourceFallback.White, out roles[5], out _, out reason))
        {
            roles.Clear();
            return false;
        }

        uint baseSampling;
        bool baseCaptured = baseColor?.Texture is XRTexture2DArray textureArray
            ? AdvancedEngineSurfaceSamplingKey.TryCapture(textureArray, out baseSampling, out reason)
            : AdvancedEngineSurfaceSamplingKey.TryCapture(baseColor?.Texture, out baseSampling, out reason);
        if (!baseCaptured ||
            !AdvancedEngineSurfaceSamplingKey.TryCapture(normal?.Texture, out uint normalSampling, out reason) ||
            !AdvancedEngineSurfaceSamplingKey.TryCapture(metallic?.Texture, out uint metallicSampling, out reason) ||
            !AdvancedEngineSurfaceSamplingKey.TryCapture(roughness?.Texture, out uint roughnessSampling, out reason) ||
            !AdvancedEngineSurfaceSamplingKey.TryCapture(opacity?.Texture, out uint opacitySampling, out reason) ||
            !AdvancedEngineSurfaceSamplingKey.TryCapture(specular?.Texture, out uint specularSampling, out reason))
        { roles.Clear(); return false; }

        record = new AdvancedEngineSurfaceRecord
        {
            SchemaVersion = AdvancedEngineSurfaceRecord.CurrentSchemaVersion,
            SurfaceKind = surfaceKind,
            RoleFlags = (baseColor is null ? 0u : 1u) | (normal is null ? 0u : 2u) |
                (metallic is null ? 0u : 4u) | (roughness is null ? 0u : 8u) | (opacity is null ? 0u : 16u) |
                (specular is null ? 0u : 32u),
            BaseColorOpacity = new Vector4(values.BaseColor, values.Opacity),
            RoughnessMetallicSpecularEmission = new Vector4(values.Roughness, values.Metallic, values.Specular, values.Emission),
            BaseColor = CreateRole(baseColor, false),
            Normal = CreateRole(normal, true),
            Metallic = CreateRole(metallic, false),
            Roughness = CreateRole(roughness, false),
            Opacity = CreateRole(opacity, false),
            Specular = CreateRole(specular, false),
            NormalControls = normalControls,
            BaseColorSampling = baseSampling, NormalSampling = normalSampling, MetallicSampling = metallicSampling,
            RoughnessSampling = roughnessSampling, OpacitySampling = opacitySampling, SpecularSampling = specularSampling,
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
