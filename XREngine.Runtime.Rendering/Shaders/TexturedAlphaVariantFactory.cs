using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>Creates source-owned browser replay materials that retain both authored alpha images.</summary>
internal static class TexturedAlphaVariantFactory
{
    public static XRMaterial Create(XRMaterial source, EStandardLitColorAuxiliaryPass pass)
    {
        if (RuntimeEngineMaterialConstructionServices.Target != EngineMaterialConstructionTarget.WebGpuCooked ||
            !TexturedAlphaSurfaceBinding.TryRead(source, out _, out string? reason))
            throw new NotSupportedException("TexturedAlpha.AuxiliarySurfaceUnsupported: exact cooked textured-alpha source inputs are required.");
        if (source.GetEffectiveTransparencyMode() != ETransparencyMode.Masked)
            throw new NotSupportedException("TexturedAlpha.AuxiliaryCoverageUnsupported: sorted alpha surfaces do not write scene or shadow depth.");
        if (pass is not (EStandardLitColorAuxiliaryPass.DepthNormal or EStandardLitColorAuxiliaryPass.ShadowDepth or
            EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth))
            throw new ArgumentOutOfRangeException(nameof(pass));
        return new XRMaterial
        {
            Name = source.Name,
            RenderPass = source.RenderPass,
            StandardLitColorSourceMaterial = source,
            StandardLitColorAuxiliaryPass = pass,
            EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1,
            RenderOptions =
            {
                CullMode = pass == EStandardLitColorAuxiliaryPass.DepthNormal ? source.RenderOptions.CullMode : ECullMode.None,
                Winding = source.RenderOptions.Winding,
                AlphaToCoverage = ERenderParamUsage.Disabled,
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, Function = source.RenderOptions.DepthTest.Function, UpdateDepth = true },
                RequiredEngineUniforms = EUniformRequirements.Camera,
            },
        };
    }
}
