using System.Numerics;
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
        /// Captures the numeric material values and the first albedo, normal, and roughness/metallic source textures.
        /// </summary>
        public static MaterialBindingSourceSnapshot Encode(XRMaterial? material)
        {
            if (material is null)
                return default;

            XRTexture? albedo = material.Textures.Count > 0 ? material.Textures[0] : null;
            XRTexture? normal = material.Textures.Count > 1 ? material.Textures[1] : null;
            XRTexture? rm = material.Textures.Count > 2 ? material.Textures[2] : null;
            uint flags = 0u;

            if (albedo is not null)
                flags |= 1u << 0;
            if (normal is not null)
                flags |= 1u << 1;
            if (rm is not null)
                flags |= 1u << 2;

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
                rm);
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
    }

    /// <summary>
    /// Renderer-neutral material row and its three texture sources.
    /// </summary>
    public readonly struct MaterialBindingSourceSnapshot(
        GPUMaterialEntry entry,
        XRTexture? albedo,
        XRTexture? normal,
        XRTexture? rm)
    {
        public GPUMaterialEntry Entry { get; } = entry;
        public XRTexture? Albedo { get; } = albedo;
        public XRTexture? Normal { get; } = normal;
        public XRTexture? RM { get; } = rm;
    }
}
