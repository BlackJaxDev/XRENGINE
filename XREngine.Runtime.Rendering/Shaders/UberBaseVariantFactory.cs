using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>Creates exact source-owned Uber auxiliary replay without deriving a substitute material.</summary>
internal static class UberBaseVariantFactory
{
    public static XRMaterial Create(XRMaterial source, EStandardLitColorAuxiliaryPass pass)
    {
        if (RuntimeEngineMaterialConstructionServices.Target != EngineMaterialConstructionTarget.WebGpuCooked ||
            source.EngineSemantic != EngineMaterialSemanticIdentity.UberBaseV1 || source.CookedUberBaseProfile is null)
            throw new NotSupportedException("UberBase.AuxiliarySourceMissing: the exact target-cooked Uber source is required.");
        if (source.GetEffectiveTransparencyMode() is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
            throw new NotSupportedException("UberBase.AuxiliaryCoverageUnsupported: sorted transparent sources do not write scene or shadow depth.");
        if (pass is not (EStandardLitColorAuxiliaryPass.DepthNormal or EStandardLitColorAuxiliaryPass.ShadowDepth or
            EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth))
            throw new ArgumentOutOfRangeException(nameof(pass));
        return new XRMaterial
        {
            Name = source.Name, RenderPass = source.RenderPass,
            StandardLitColorSourceMaterial = source, StandardLitColorAuxiliaryPass = pass,
            EngineSemantic = EngineMaterialSemanticIdentity.UberBaseV1,
            RenderOptions =
            {
                CullMode = pass == EStandardLitColorAuxiliaryPass.DepthNormal ? source.RenderOptions.CullMode : ECullMode.None,
                Winding = source.RenderOptions.Winding, AlphaToCoverage = ERenderParamUsage.Disabled,
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, Function = source.RenderOptions.DepthTest.Function, UpdateDepth = true },
                RequiredEngineUniforms = EUniformRequirements.Camera,
            },
        };
    }
}
