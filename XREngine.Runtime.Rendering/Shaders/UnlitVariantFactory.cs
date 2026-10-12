using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>Replays the same cooked unlit geometry and alpha coverage in normal and shadow passes.</summary>
internal static class UnlitVariantFactory
{
    public static XRMaterial Create(XRMaterial source, EStandardLitColorAuxiliaryPass pass)
    {
        string? reason = null;
        if (RuntimeEngineMaterialConstructionServices.Target != EngineMaterialConstructionTarget.WebGpuCooked ||
            !EngineUnlitSurfaceBinding.TryRead(source, out EngineUnlitSurface surface, out reason))
            throw new NotSupportedException($"Unlit.AuxiliarySurfaceUnsupported: {reason}");
        if (pass is not (EStandardLitColorAuxiliaryPass.DepthNormal or EStandardLitColorAuxiliaryPass.ShadowDepth or
            EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth) ||
            pass != EStandardLitColorAuxiliaryPass.DepthNormal &&
                surface.Semantic != EngineMaterialSemanticIdentity.UnlitAlphaTextureV4)
            throw new ArgumentOutOfRangeException(nameof(pass));
        if (source.IsTransparentLike())
            throw new NotSupportedException("Unlit.AuxiliaryCoverageUnsupported: painter-ordered unlit surfaces do not write scene or shadow depth.");
        return new XRMaterial
        {
            Name = source.Name,
            RenderPass = source.RenderPass,
            StandardLitColorSourceMaterial = source,
            StandardLitColorAuxiliaryPass = pass,
            EngineSemantic = source.EngineSemantic,
            RenderOptions =
            {
                CullMode = pass == EStandardLitColorAuxiliaryPass.DepthNormal ? source.RenderOptions.CullMode : ECullMode.None,
                Winding = source.RenderOptions.Winding,
                AlphaToCoverage = ERenderParamUsage.Disabled,
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = source.RenderOptions.DepthTest.Function,
                    UpdateDepth = true,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera,
            },
        };
    }
}
