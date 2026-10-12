using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial : IPostCookedBinaryDeserialize
{
    private bool _preserveCookedUnlitParameters;

    /// <summary>Restores subscriptions and admitted texture metadata after reflection-cooked material hydration.</summary>
    public void OnPostCookedBinaryDeserialize()
    {
        if (this is PublishedUnlitMaterial unlit)
        {
            if (!EngineSemantic.IsUnlit())
                throw new InvalidDataException("CookedMaterial.UnlitSemanticInvalid: recook this target material with an exact versioned unlit semantic.");
            (unlit.PublishedUnlitTextureProfile ?? throw new InvalidDataException(
                "CookedMaterial.UnlitProfileMissing: recook this target material with its versioned texture profile."))
                .RestoreDecodedTextures(this);
            RestoreCookedParameterSubscriptions();
            PreShadersSet();
            _preserveCookedUnlitParameters = true;
            try { PostShadersSet(); }
            finally { _preserveCookedUnlitParameters = false; }
            if (!EngineUnlitSurfaceBinding.TryRead(this, out _, out string? reason))
                throw new InvalidDataException($"CookedMaterial.UnlitInvalid: {reason}");
            return;
        }
        if (GetType() != typeof(XRMaterial) && GetType() != typeof(PublishedUberBaseMaterial) ||
            !EngineSemantic.IsAuthoredLit() && !EngineSemantic.IsUnlit() && CookedOutlineProfile is null && CookedUberBaseProfile is null)
            return;
        CookedOutlineProfile?.RestoreDecodedTextures(this);
        CookedUberBaseProfile?.RestoreDecodedTextures(this);
        RestoreCookedParameterSubscriptions();
        PreShadersSet();
        PostShadersSet();
    }

    /// <summary>
    /// Authors an opaque deferred PBR surface with a canonical desktop GLSL stage
    /// and an explicit per-material WGSL cook contract. The publisher selects the
    /// browser companion from this material's live factors and texture roles.
    /// </summary>
    public static XRMaterial CreateAuthoredLitPbrColorMaterial(ColorF4 color,
        float specular = 1, float roughness = 1, float metallic = 0, float emission = 0)
    {
        RequireAuthoredLitDesktopConstruction();
        if (color.A != 1 || !float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B) ||
            !float.IsFinite(specular) || !float.IsFinite(roughness) || !float.IsFinite(metallic) || !float.IsFinite(emission))
            throw new ArgumentException("Authored opaque PBR factors must be finite and opacity must equal one.");
        ShaderVar[] parameters = CreateDeferredLitDefaults(color, specular, roughness, metallic, emission);
        XRShader shader = ShaderHelper.LitColorFragDeferred()
            ?? throw new InvalidOperationException("The canonical desktop PBR color fragment is unavailable.");
        XRMaterial material = new(parameters, shader);
        material.Parameters = parameters;
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        material.EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitV1;
        return material;
    }

    /// <summary>Authors the corresponding opaque textured PBR surface and normal-map feature.</summary>
    public static XRMaterial CreateAuthoredLitPbrTextureMaterial(XRTexture2D baseColor, XRTexture2D? normal = null,
        XRTexture2D? metallic = null, XRTexture2D? roughness = null, ColorF4? tint = null,
        float specular = 1, float roughnessFactor = 1, float metallicFactor = 0, float emission = 0)
    {
        RequireAuthoredLitDesktopConstruction();
        XRMaterial material = CreateLitPbrTextureMaterial(baseColor, normal, metallic, roughness, tint,
            specular, roughnessFactor, metallicFactor, emission);
        material.EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitV1;
        return material;
    }

    /// <summary>Authors the existing uniform-alpha PBR color contract for a per-material target cook.</summary>
    public static XRMaterial CreateAuthoredLitPbrColorCoverageMaterial(ColorF4 color,
        ETransparencyMode transparencyMode = ETransparencyMode.AlphaBlend, float alphaCutoff = 0.5f,
        float specular = 1, float roughness = 0.5f, float metallic = 0, float emission = 0)
    {
        RequireAuthoredLitDesktopConstruction();
        if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B) ||
            !float.IsFinite(specular) || !float.IsFinite(roughness) || !float.IsFinite(metallic) || !float.IsFinite(emission))
            throw new ArgumentException("Authored PBR color and lighting factors must be finite.");
        XRMaterial material = CreateLitColorCoverageMaterial(color, transparencyMode, alphaCutoff);
        material.Parameter<ShaderFloat>("Specular")!.Value = specular;
        material.Parameter<ShaderFloat>("Roughness")!.Value = roughness;
        material.Parameter<ShaderFloat>("Metallic")!.Value = metallic;
        material.Parameter<ShaderFloat>("Emission")!.Value = emission;
        material.EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitV2;
        return material;
    }

    private static void RequireAuthoredLitDesktopConstruction()
    {
        if (RuntimeEngineMaterialConstructionServices.Target != EngineMaterialConstructionTarget.DesktopGlsl)
            throw new NotSupportedException("Authored lit materials must be created with their desktop GLSL source before a target-specific browser cook.");
    }
}
