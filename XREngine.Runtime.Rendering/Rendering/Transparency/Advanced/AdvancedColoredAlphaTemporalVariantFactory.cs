using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

/// <summary>Creates the supported built-in forward colored-alpha material and its temporal pair.</summary>
public static class AdvancedColoredAlphaTemporalVariantFactory
{
    public static XRMaterial Create(ColorF4 color)
    {
        XRMaterial source = XRMaterial.CreateLitColorMaterial(color, deferred: false);
        source.EnableTransparency((int)EDefaultRenderPass.TransparentForward);
        XRMaterial velocityMaterial = CreateVariant(source.Parameters, "AdvancedColoredAlphaMotionVectors.fs");
        velocityMaterial.SettingUniforms += BindTemporalMatrices;
        XRMaterial reactiveMaterial = CreateVariant(source.Parameters, "AdvancedColoredAlphaReactiveMask.fs");
        // Preserve stronger reactive coverage from earlier producers and overlapping draws.
        reactiveMaterial.RenderOptions.BlendModeAllDrawBuffers = new BlendMode
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbEquation = EBlendEquationMode.Max,
            AlphaEquation = EBlendEquationMode.Max,
            RgbSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
        };
        source.AdvancedLatePassMetadata = new(
            EAdvancedLatePassKind.SortedAlpha,
            participatesInMotionVectors: true,
            isOrderDependent: true)
        {
            TemporalVelocityMaterial = velocityMaterial,
            TemporalReactiveMaskMaterial = reactiveMaterial,
            RequiresRigidTemporalGeometry = true,
            TemporalSourceShaderRevision = source.ShaderStateRevision,
            TemporalCoverageParameter = source.Parameters[0],
        };
        return source;
    }

    private static void BindTemporalMatrices(XRMaterialBase material, XRRenderProgram program)
    {
        if (!VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var data))
            throw new InvalidOperationException("Advanced transparent velocity requires a published temporal snapshot.");

        program.Uniform("CurrViewProjection", data.CurrViewProjectionUnjittered);
        program.Uniform("PrevViewProjection", data.LeftEyeHistoryReady
            ? data.PrevViewProjectionUnjittered : data.CurrViewProjectionUnjittered);
        program.Uniform("RightEyeCurrViewProjection", data.RightEyeCurrViewProjectionUnjittered);
        program.Uniform("RightEyePrevViewProjection", data.RightEyeHistoryReady
            ? data.RightEyePrevViewProjectionUnjittered : data.RightEyeCurrViewProjectionUnjittered);
        program.Uniform("LeftEyeHistoryReady", data.LeftEyeHistoryReady);
        program.Uniform("RightEyeHistoryReady", data.RightEyeHistoryReady);
    }

    private static XRMaterial CreateVariant(ShaderVar[] sourceParameters, string fragmentShader)
    {
        // XRMaterial copies the array while retaining its parameter objects.
        // MatColor alpha edits must update color and temporal coverage together.
        return new XRMaterial(sourceParameters, XRShader.EngineShader(Path.Combine(AdvancedRenderPipeline.SceneShaderPath, fragmentShader), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters
            {
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, Function = EComparison.Lequal, UpdateDepth = false },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
            },
        };
    }
}
