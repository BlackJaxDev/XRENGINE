using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Materials
{
    /// <summary>
    /// Produces the renderer-neutral portion of a GPU material-table row.
    /// Texture resource words intentionally remain zero until the selected backend publishes them.
    /// </summary>
    public static class MaterialBindingSourceEncoder
    {
        /// <summary>
        /// Captures material values and source textures through their surface semantics.
        /// </summary>
        public static MaterialBindingSourceSnapshot Encode(XRMaterial? material)
        {
            if (material is null)
                return default;

            bool hasSurfaceBindings = material.SurfaceTextureBindings.Length != 0;
            XRTexture? albedo = material.GetSurfaceTexture(EMaterialTextureSemantic.BaseColor)?.Texture;
            XRTexture? normal = material.GetSurfaceTexture(EMaterialTextureSemantic.Normal)?.Texture;
            XRTexture? rm = material.GetSurfaceTexture(EMaterialTextureSemantic.Metallic)?.Texture ??
                material.GetSurfaceTexture(EMaterialTextureSemantic.Roughness)?.Texture;
            MaterialSurfaceTextureBinding? emissiveBinding = material.GetSurfaceTexture(EMaterialTextureSemantic.Emissive);
            XRTexture? emissive = emissiveBinding?.Texture;
            // A separate opacity map only matters for coverage. A map that is also the base
            // color adds nothing, because coverage already reads the albedo alpha.
            XRTexture? opacity = material.GetSurfaceTexture(EMaterialTextureSemantic.Opacity)?.Texture;
            bool uberSource = !hasSurfaceBindings && IsUberSource(material);
            if (uberSource)
            {
                // Converted uber materials name their samplers by role. Their texture
                // order follows the source shader, so slot 2 is often a matcap or mask.
                albedo = FindTextureBySampler(material, "_MainTex");
                normal = FindTextureBySampler(material, "_BumpMap");
                rm = null;
                emissive = FindTextureBySampler(material, "_EmissionMap");
                opacity = null;
            }
            else if (!hasSurfaceBindings)
            {
                albedo = material.Textures.Count > 0 ? material.Textures[0] : null;
                normal = material.Textures.Count > 1 ? material.Textures[1] : null;
                rm = material.Textures.Count > 2 ? material.Textures[2] : null;
                opacity = null;
            }
            if (ReferenceEquals(opacity, albedo))
                opacity = null;
            uint flags = 0u;

            if (albedo is not null)
                flags |= 1u << 0;
            if (normal is not null)
                flags |= 1u << 1;
            if (rm is not null)
                flags |= 1u << 2;
            if (material.GetEffectiveTransparencyMode() is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage)
            {
                flags |= GPUMaterialEntry.MaskedCoverageFlag;
                if (opacity is not null)
                    flags |= GPUMaterialEntry.OpacityTextureFlag;
            }
            else
            {
                // Only masked coverage reads the opacity map.
                opacity = null;
            }

            SurfaceDetailControls surfaceDetail = ResolveSurfaceDetail(material, ref normal, uberSource);
            if (normal is not null)
                flags |= 1u << 1;
            flags |= surfaceDetail.Flags;

            return new MaterialBindingSourceSnapshot(
                new GPUMaterialEntry
                {
                    Flags = flags,
                    BaseColorOpacity = ResolveBaseColorOpacity(material),
                    RMSE = ResolveRmse(material),
                    AlphaCutoff = material.AlphaCutoff,
                    SurfaceDetailHeightScale = surfaceDetail.HeightScale,
                    ParallaxScale = surfaceDetail.ParallaxScale,
                },
                albedo,
                normal,
                rm,
                emissive,
                ResolveEmissionColor(material),
                ResolveEmissionStrength(material),
                ResolveEmissionTextureMetadata(emissiveBinding),
                emissiveBinding?.UvScaleOffset ?? new Vector4(1.0f, 1.0f, 0.0f, 0.0f),
                emissiveBinding?.UvRotation ?? 0.0f,
                opacity);
        }

        private readonly record struct SurfaceDetailControls(uint Flags, float HeightScale, float ParallaxScale);

        /// <summary>
        /// Resolves the height-map controls of the surface-detail (normal) slot. Imported height and
        /// bump maps (<c>NormalMapMode</c> 1) have no normal surface binding, so their legacy slot
        /// (Texture1) supplies the texture. <c>ParallaxScale</c> marks parallax occlusion mapping.
        /// </summary>
        private static SurfaceDetailControls ResolveSurfaceDetail(XRMaterial material, ref XRTexture? normal, bool uberSource)
        {
            if ((material.Parameter<ShaderInt>("NormalMapMode")?.Value ?? 0) != 1)
                return default;

            if (normal is null && !uberSource && material.Textures.Count > 1)
                normal = material.Textures[1];
            if (normal is null)
                return default;

            uint flags = GPUMaterialEntry.HeightMapFlag;
            float heightScale = material.Parameter<ShaderFloat>("HeightMapScale")?.Value ?? 1.0f;
            float parallaxScale = 0.0f;
            if (!uberSource && material.Parameter<ShaderFloat>("ParallaxScale") is { Value: > 0.0f } parallax)
            {
                parallaxScale = parallax.Value;
                uint maxSteps = (uint)Math.Clamp(material.Parameter<ShaderInt>("ParallaxMaxSteps")?.Value ?? 32, 1, 255);
                uint minSteps = (uint)Math.Clamp(material.Parameter<ShaderInt>("ParallaxMinSteps")?.Value ?? 8, 1, 255);
                uint refineSteps = (uint)Math.Clamp(material.Parameter<ShaderInt>("ParallaxRefineSteps")?.Value ?? 5, 0, 15);
                flags |= GPUMaterialEntry.ParallaxFlag |
                    (maxSteps << GPUMaterialEntry.ParallaxMaxStepsShift) |
                    (minSteps << GPUMaterialEntry.ParallaxMinStepsShift) |
                    (refineSteps << GPUMaterialEntry.ParallaxRefineStepsShift);
                if ((material.Parameter<ShaderInt>("ParallaxSilhouette")?.Value ?? 0) != 0)
                    flags |= GPUMaterialEntry.ParallaxSilhouetteFlag;
            }

            return new SurfaceDetailControls(flags, heightScale, parallaxScale);
        }

        /// <summary>True for a converted uber material, which carries the uber lighting-mode parameter.</summary>
        private static bool IsUberSource(XRMaterial material)
            => material.Parameter<ShaderInt>("_LightingMode") is not null;

        private static XRTexture? FindTextureBySampler(XRMaterial material, string samplerName)
        {
            EventList<XRTexture?> textures = material.Textures;
            for (int index = 0; index < textures.Count; index++)
                if (textures[index] is { } texture &&
                    string.Equals(texture.SamplerName, samplerName, StringComparison.Ordinal))
                    return texture;
            return null;
        }

        private static Vector4 ResolveBaseColorOpacity(XRMaterial material)
        {
            // Converted uber materials tint the base map with _Color.
            if (material.Parameter<ShaderVector4>("_Color") is { } uberColor)
                return uberColor.Value;

            Vector3 baseColor = material.Parameter<ShaderVector3>("BaseColor")?.Value ?? Vector3.One;
            float opacity = material.Parameter<ShaderFloat>("Opacity")?.Value ?? 1.0f;

            if (material.Parameter<ShaderVector4>("BaseColor") is { } baseColor4)
            {
                Vector4 value = baseColor4.Value;
                baseColor = new Vector3(value.X, value.Y, value.Z);
                opacity = value.W;
            }
            else if (material.Parameter<ShaderVector4>("MatColor") is { } matColor)
            {
                Vector4 value = matColor.Value;
                baseColor = new Vector3(value.X, value.Y, value.Z);
                opacity = value.W;
            }

            return new Vector4(baseColor, opacity);
        }

        private static Vector4 ResolveRmse(XRMaterial material)
            => new(
                material.Parameter<ShaderFloat>("Roughness")?.Value ?? 1.0f,
                material.Parameter<ShaderFloat>("Metallic")?.Value ?? 0.0f,
                material.Parameter<ShaderFloat>("Specular")?.Value ?? 1.0f,
                material.Parameter<ShaderFloat>("Emission")?.Value ?? 0.0f);

        private static Vector4 ResolveEmissionColor(XRMaterial material)
        {
            Vector3 color = material.EmissiveColor ?? Vector3.Zero;
            return new Vector4(color, material.EmissiveColor.HasValue ? 1.0f : 0.0f);
        }

        private static float ResolveEmissionStrength(XRMaterial material)
            => material.EmissionStrength ?? material.Parameter<ShaderFloat>("Emission")?.Value ?? 0.0f;

        private static Vector4 ResolveEmissionTextureMetadata(MaterialSurfaceTextureBinding? binding)
        {
            if (binding is null)
                return Vector4.Zero;

            bool sourceFormatDecodesSrgb = binding.Texture is XRTexture2D texture &&
                texture.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8;
            return new Vector4(
                binding.TexCoordSet,
                binding.IsSrgb && !sourceFormatDecodesSrgb ? 1.0f : 0.0f,
                0.0f,
                0.0f);
        }
    }

}
