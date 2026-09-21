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
            if (!hasSurfaceBindings)
            {
                albedo = material.Textures.Count > 0 ? material.Textures[0] : null;
                normal = material.Textures.Count > 1 ? material.Textures[1] : null;
                rm = material.Textures.Count > 2 ? material.Textures[2] : null;
            }
            uint flags = 0u;

            if (albedo is not null)
                flags |= 1u << 0;
            if (normal is not null)
                flags |= 1u << 1;
            if (rm is not null)
                flags |= 1u << 2;
            if (material.GetEffectiveTransparencyMode() is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage)
                flags |= GPUMaterialEntry.MaskedCoverageFlag;

            return new MaterialBindingSourceSnapshot(
                new GPUMaterialEntry
                {
                    Flags = flags,
                    BaseColorOpacity = ResolveBaseColorOpacity(material),
                    RMSE = ResolveRmse(material),
                    AlphaCutoff = material.AlphaCutoff,
                },
                albedo,
                normal,
                rm,
                emissive,
                ResolveEmissionColor(material),
                ResolveEmissionStrength(material),
                ResolveEmissionTextureMetadata(emissiveBinding),
                emissiveBinding?.UvScaleOffset ?? new Vector4(1.0f, 1.0f, 0.0f, 0.0f),
                emissiveBinding?.UvRotation ?? 0.0f);
        }

        private static Vector4 ResolveBaseColorOpacity(XRMaterial material)
        {
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
