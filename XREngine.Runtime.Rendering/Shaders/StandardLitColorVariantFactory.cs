using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>Creates source-owned auxiliary materials with identical uniform-alpha coverage.</summary>
internal static class StandardLitColorVariantFactory
{
    public static XRMaterial? Create(XRMaterial source, EStandardLitColorAuxiliaryPass pass)
    {
        if (!source.EngineSemantic.IsColorCoverage())
            return null;
        bool authored = source.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2;
        bool valid = authored
            ? StandardLitColorSurfaceBinding.TryCreateAuthoredCooked(source, out _, out string? reason)
            : StandardLitColorSurfaceBinding.TryCreate(source, out _, out reason);
        if (!valid)
            throw new NotSupportedException($"StandardLitColor.SurfaceUnsupported: {reason}");
        if (source.GetEffectiveTransparencyMode() is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
            throw new NotSupportedException("StandardLitColor.AuxiliaryCoverageUnsupported: blended surfaces do not write scene or shadow depth.");

        XRMaterial variant;
        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked)
        {
            if (!authored && source.Shaders.Count != 0)
                throw new NotSupportedException("StandardLitColor.SourceUnsupported: cooked variants require source-free authored materials.");
            variant = new XRMaterial();
        }
        else
        {
            XRShader? fragment = source.GetShader(EShaderType.Fragment);
            XRShader? shader = pass == EStandardLitColorAuxiliaryPass.DepthNormal
                ? ShaderHelper.GetDepthNormalPrePassForwardVariant(fragment)
                : pass == EStandardLitColorAuxiliaryPass.PointShadowDepth
                    ? ShaderHelper.GetPointShadowCasterForwardVariant(fragment)
                    : ShaderHelper.GetShadowCasterForwardVariant(fragment);
            if (shader is null)
                throw new NotSupportedException("StandardLitColor.SourceUnsupported: no coverage-preserving auxiliary shader is available.");
            variant = new XRMaterial(shader);
        }

        variant.Name = source.Name;
        variant.Parameters = source.Parameters;
        variant.RenderPass = source.RenderPass;
        variant.StandardLitColorSourceMaterial = source;
        variant.StandardLitColorAuxiliaryPass = pass;
        variant.EngineSemantic = EngineMaterialSemanticIdentity.StandardLitColorV2;
        variant.RenderOptions = new RenderingParameters
        {
            CullMode = pass is EStandardLitColorAuxiliaryPass.ShadowDepth or EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth
                ? ECullMode.None : source.RenderOptions.CullMode,
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
        };
        return variant;
    }
}
