using System.Text.Json;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineLitMaterialShaderGenerator
{
    /// <summary>
    /// Derives the cooked shader profile from a real engine material's modeled PBR
    /// factors and semantic texture bindings. No desktop source is parsed as WGSL.
    /// </summary>
    public static bool TryPlan(XRMaterial material, ShaderCompileTarget target,
        out EngineLitMaterialShaderPlan plan, out string? reason)
        => TryPlanCore(material, target, null, out plan, out reason);

    /// <summary>Permits fully equivalent serialized texture aliases during a detached offline cook.</summary>
    public static bool TryPlanForCook(XRMaterial material, ShaderCompileTarget target,
        Func<XRTexture2D, XRTexture2D, bool> equivalentTexture,
        out EngineLitMaterialShaderPlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(equivalentTexture);
        return TryPlanCore(material, target, equivalentTexture, out plan, out reason);
    }

    private static bool TryPlanCore(XRMaterial material, ShaderCompileTarget target,
        Func<XRTexture2D, XRTexture2D, bool>? equivalentTexture,
        out EngineLitMaterialShaderPlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(material);
        plan = default;
        if (!material.EngineSemantic.IsAuthoredLit() || material.ID == Guid.Empty)
        {
            reason = "The material must have a supported authored lit semantic and a persistent identity.";
            return false;
        }
        if (target != ShaderCompileTarget.WebGPUWgsl)
        {
            reason = $"Authored lit materials have no generator for target '{target}'.";
            return false;
        }
        if (material.UberAuthoredState.Features.Length != 0 || material.UberAuthoredState.Properties.Length != 0 ||
            !material.RequestedUberVariant.IsEmpty)
        {
            reason = "Authored lit materials admit the engine's PBR factors and supported surface texture roles, but no Uber feature or static-property override.";
            return false;
        }
        if (material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitTexturedV1)
        {
            AuthoredTexturedSurface textured;
            if (equivalentTexture is null)
            {
                if (!AuthoredTexturedSurfaceBinding.TryRead(material, out textured, out reason)) return false;
            }
            else if (!AuthoredTexturedSurfaceBinding.TryReadForCook(material, equivalentTexture, out textured, out reason)) return false;
            plan = EngineAuthoredTexturedShaderGenerator.Plan(CookName(material.ID), textured.TextureFlags,
                textured.Values.TransparencyMode switch
                {
                    ETransparencyMode.Opaque => "opaque",
                    ETransparencyMode.Masked => "masked",
                    _ => "alpha-blend",
                }, target);
            return true;
        }
        if (material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1)
        {
            TexturedAlphaSurface alpha;
            if (equivalentTexture is null)
            {
                if (!TexturedAlphaSurfaceBinding.TryRead(material, out alpha, out reason)) return false;
            }
            else if (!TexturedAlphaSurfaceBinding.TryReadForCook(material, equivalentTexture, out alpha, out reason)) return false;
            plan = EngineTexturedAlphaShaderGenerator.Plan(CookName(material.ID),
                alpha.Values.TransparencyMode == ETransparencyMode.Masked ? "masked" : "alpha-blend", target);
            return true;
        }
        if (material.SurfaceTextureBindings.Length == 0)
        {
            if (!StandardLitColorSurfaceBinding.TryCreateAuthoredCooked(material, out _, out reason)) return false;
            string coverage = material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2
                ? material.GetEffectiveTransparencyMode() switch
                {
                    ETransparencyMode.Opaque => "opaque-coverage",
                    ETransparencyMode.Masked => "masked",
                    ETransparencyMode.AlphaBlend => "alpha-blend",
                    ETransparencyMode.PremultipliedAlpha => "premultiplied-alpha",
                    ETransparencyMode.Additive => "additive",
                    _ => throw new InvalidOperationException("The validated color coverage mode has no target surface profile."),
                } : "opaque";
            plan = Plan(CookName(material.ID), "lit", coverage, "tint", "vertex", target);
            return true;
        }
        if (material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2)
        {
            reason = "AuthoredLitV2 supports uniform-alpha color coverage only; texture maps require the unchanged opaque V1 surface contract.";
            return false;
        }
        StandardLitTextureSurface surface;
        if (equivalentTexture is null)
        {
            if (!StandardLitTextureSurfaceBinding.TryCreateAuthoredCooked(material,
                out StandardLitTextureSurfaceBinding? texture, out reason) ||
                !texture!.TryRead(out surface, out reason)) return false;
        }
        else if (!StandardLitTextureSurfaceBinding.TryReadAuthoredForCook(material, equivalentTexture,
            out surface, out reason)) return false;
        plan = Plan(CookName(material.ID), "lit", "opaque", "texture",
            surface.Normal is null ? "vertex" : "texture", target);
        return true;
    }

    /// <summary>Stable artifact name for one persistent authored material.</summary>
    public static string CookName(Guid materialId) => materialId == Guid.Empty
        ? throw new ArgumentException("A persistent material ID is required.", nameof(materialId))
        : "mat-" + materialId.ToString("N");

    /// <summary>Emits the target material source to stage beside the versioned engine Slang frontend.</summary>
    public static string MaterialRecipeJson(EngineLitMaterialShaderPlan plan)
        => plan.AuthoredTextureFlags != 0 ? JsonSerializer.Serialize(new
        {
            schemaVersion = 2, name = plan.Name, shadingModel = "lit", surface = plan.Surface,
            baseColor = "authored-textured", normal = plan.UsesNormalTexture ? "texture" : "vertex",
            textureFlags = plan.AuthoredTextureFlags,
        }, new JsonSerializerOptions { WriteIndented = true }) : JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            name = plan.Name,
            shadingModel = "lit",
            surface = plan.Surface,
            baseColor = plan.SemanticSchemaIdentity == EngineTexturedAlphaShaderGenerator.Schema
                ? "texture-alpha" : plan.UsesBaseColorTexture ? "texture" : "tint",
            normal = plan.UsesNormalTexture ? "texture" : "vertex",
        }, new JsonSerializerOptions { WriteIndented = true });
}
