using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>Retains the mapped world normal when the cooked pipeline replays an opaque texture surface.</summary>
internal static class StandardLitTextureVariantFactory
{
    public static XRMaterial? CreateDepthNormal(XRMaterial source)
    {
        if (RuntimeEngineMaterialConstructionServices.Target != EngineMaterialConstructionTarget.WebGpuCooked ||
            source.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1)
            return null;
        if (!StandardLitTextureSurfaceBinding.TryCreate(source, out StandardLitTextureSurfaceBinding? binding, out string? reason) ||
            !binding!.TryRead(out StandardLitTextureSurface surface, out reason))
            throw new NotSupportedException($"StandardLitTexture.SurfaceUnsupported: {reason}");
        if (surface.Normal is null) return null;
        if (source.Shaders.Count != 0)
            throw new NotSupportedException("StandardLitTexture.SourceUnsupported: cooked normal replay requires a source-free material.");
        return new XRMaterial
        {
            Name = source.Name,
            RenderPass = source.RenderPass,
            StandardLitColorSourceMaterial = source,
            StandardLitColorAuxiliaryPass = EStandardLitColorAuxiliaryPass.DepthNormal,
            EngineSemantic = EngineMaterialSemanticIdentity.StandardLitTextureV1,
            RenderOptions =
            {
                CullMode = source.RenderOptions.CullMode,
                Winding = source.RenderOptions.Winding,
                AlphaToCoverage = ERenderParamUsage.Disabled,
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, Function = EComparison.Lequal, UpdateDepth = true },
                RequiredEngineUniforms = EUniformRequirements.Camera,
            },
        };
    }
}
