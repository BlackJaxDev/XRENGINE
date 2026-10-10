using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    /// <summary>
    /// Authors a canonical forward normal/height or specular-map surface. Without an
    /// opacity image diffuse alpha is output without clipping; an opacity image always
    /// clips diffuse alpha times opacity red strictly below the cutoff, including blending.
    /// </summary>
    public static XRMaterial CreateAuthoredLitTexturedMaterial(XRTexture2D baseColor, XRTexture2D? normal = null,
        XRTexture2D? specularMap = null, XRTexture2D? opacityMask = null,
        ETransparencyMode transparencyMode = ETransparencyMode.Opaque, float alphaCutoff = 0.5f,
        float specular = 1, float roughness = 0.9f, float metallic = 0, float emission = 0, float shininess = 64,
        int normalMapMode = 0, float heightMapScale = 1)
    {
        RequireAuthoredLitDesktopConstruction();
        ArgumentNullException.ThrowIfNull(baseColor);
        if (normal is null && specularMap is null)
            throw new ArgumentException("An authored textured surface requires a normal or specular image.");
        if (opacityMask is null ? transparencyMode is not (ETransparencyMode.Opaque or ETransparencyMode.AlphaBlend)
            : transparencyMode is not (ETransparencyMode.Masked or ETransparencyMode.AlphaBlend))
            throw new ArgumentOutOfRangeException(nameof(transparencyMode), "Opacity images require masked or sorted alpha-blend coverage; surfaces without opacity images require opaque or sorted alpha-blend coverage.");
        if (!float.IsFinite(alphaCutoff) || !float.IsFinite(specular) || !float.IsFinite(roughness) ||
            !float.IsFinite(metallic) || !float.IsFinite(emission) || !float.IsFinite(shininess) || !float.IsFinite(heightMapScale))
            throw new ArgumentException("Authored textured factors and cutoff must be finite.");
        if (normalMapMode is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(normalMapMode), "Normal-map mode must be RGB normal (zero) or red-channel height (one).");
        RequireLinear(normal, nameof(normal));
        RequireLinear(specularMap, nameof(specularMap));
        RequireLinear(opacityMask, nameof(opacityMask));

        List<ShaderVar> parameters =
        [
            new ShaderFloat(specular, "MatSpecularIntensity"), new ShaderFloat(shininess, "MatShininess"),
            new ShaderFloat(roughness, "Roughness"), new ShaderFloat(metallic, "Metallic"), new ShaderFloat(emission, "Emission"),
        ];
        if (opacityMask is not null) parameters.Add(new ShaderFloat(alphaCutoff, "AlphaCutoff"));
        if (normal is not null)
        {
            parameters.Add(new ShaderInt(normalMapMode, "NormalMapMode"));
            parameters.Add(new ShaderFloat(heightMapScale, "HeightMapScale"));
        }
        List<XRTexture?> textures = [baseColor];
        List<MaterialSurfaceTextureBinding> bindings = [CreateStandardSurfaceBinding(EMaterialTextureSemantic.BaseColor, baseColor)];
        AddRole(normal, EMaterialTextureSemantic.Normal);
        AddRole(specularMap, EMaterialTextureSemantic.Specular);
        AddRole(opacityMask, EMaterialTextureSemantic.Opacity);
        int flags = (normal is null ? 0 : 1) | (specularMap is null ? 0 : 2) | (opacityMask is null ? 0 : 4);
        XRShader shader = flags switch
        {
            1 => ShaderHelper.LitTextureNormalFragForward(),
            2 => ShaderHelper.LitTextureSpecFragForward(),
            3 => ShaderHelper.LitTextureNormalSpecFragForward(),
            5 => ShaderHelper.LitTextureNormalAlphaFragForward(),
            6 => ShaderHelper.LitTextureSpecAlphaFragForward(),
            7 => ShaderHelper.LitTextureNormalSpecAlphaFragForward(),
            _ => throw new InvalidOperationException("Unsupported authored texture roles."),
        };
        ShaderVar[] values = [.. parameters];
        XRMaterial material = new AuthoredTexturedMaterial(values, [.. textures], shader);
        material.AlphaCutoff = alphaCutoff;
        material.TransparencyMode = transparencyMode;
        material.ApplyTransparencyState();
        material.Parameters = values;
        material.SurfaceTextureBindings = [.. bindings];
        material.EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitTexturedV1;
        return material;

        void AddRole(XRTexture2D? texture, EMaterialTextureSemantic semantic)
        {
            if (texture is null) return;
            textures.Add(texture);
            bindings.Add(CreateStandardSurfaceBinding(semantic, texture));
        }

        static void RequireLinear(XRTexture2D? texture, string name)
        {
            if (texture?.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8)
                throw new ArgumentException("Normal, height, specular and opacity images require linear texture storage.", name);
        }
    }
}
