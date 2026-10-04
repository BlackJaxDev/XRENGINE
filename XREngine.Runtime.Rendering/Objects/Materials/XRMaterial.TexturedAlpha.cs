using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    /// <summary>Authors the canonical diffuse-alpha times red-mask forward surface with exact target-cooked companions.</summary>
    public static XRMaterial CreateAuthoredLitTexturedAlphaMaterial(XRTexture2D baseColor, XRTexture2D opacityMask,
        ETransparencyMode transparencyMode = ETransparencyMode.Masked, float alphaCutoff = 0.5f,
        float specular = 1, float roughness = 0.9f, float metallic = 0, float emission = 0, float shininess = 64)
    {
        RequireAuthoredLitDesktopConstruction();
        ArgumentNullException.ThrowIfNull(baseColor);
        ArgumentNullException.ThrowIfNull(opacityMask);
        if (transparencyMode is not (ETransparencyMode.Masked or ETransparencyMode.AlphaBlend))
            throw new ArgumentOutOfRangeException(nameof(transparencyMode), "Textured alpha supports masked and sorted alpha-blend surfaces.");
        if (!float.IsFinite(alphaCutoff) || !float.IsFinite(specular) || !float.IsFinite(roughness) ||
            !float.IsFinite(metallic) || !float.IsFinite(emission) || !float.IsFinite(shininess))
            throw new ArgumentException("Textured-alpha factors and cutoff must be finite.");
        if (opacityMask.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8)
            throw new ArgumentException("The red opacity mask must use linear texture storage.", nameof(opacityMask));
        ShaderVar[] parameters =
        [
            new ShaderFloat(specular, "MatSpecularIntensity"), new ShaderFloat(shininess, "MatShininess"),
            new ShaderFloat(alphaCutoff, "AlphaCutoff"), new ShaderFloat(roughness, "Roughness"),
            new ShaderFloat(metallic, "Metallic"), new ShaderFloat(emission, "Emission"),
        ];
        XRMaterial material = new AuthoredTexturedAlphaMaterial(parameters, [baseColor, opacityMask], ShaderHelper.LitTextureAlphaFragForward());
        material.Parameters = parameters;
        material.AlphaCutoff = alphaCutoff;
        material.TransparencyMode = transparencyMode;
        material.ApplyTransparencyState();
        material.SurfaceTextureBindings =
        [
            CreateStandardSurfaceBinding(EMaterialTextureSemantic.BaseColor, baseColor),
            CreateStandardSurfaceBinding(EMaterialTextureSemantic.Opacity, opacityMask),
        ];
        material.EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1;
        return material;
    }
}
