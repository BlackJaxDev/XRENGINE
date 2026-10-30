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
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null)
            throw new InvalidOperationException("Advanced transparent velocity requires an active pipeline output.");

        if (pipeline.RenderState.TemporalAuthoringViewSet is not null &&
            VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(pipeline, out var data))
        {
            program.Uniform("CurrViewProjection", data.CurrViewProjectionUnjittered);
            program.Uniform("PrevViewProjection", data.LeftEyeHistoryReady
                ? data.PrevViewProjectionUnjittered : data.CurrViewProjectionUnjittered);
            program.Uniform("RightEyeCurrViewProjection", data.RightEyeCurrViewProjectionUnjittered);
            program.Uniform("RightEyePrevViewProjection", data.RightEyeHistoryReady
                ? data.RightEyePrevViewProjectionUnjittered : data.RightEyeCurrViewProjectionUnjittered);
            program.Uniform("LeftEyeHistoryReady", data.LeftEyeHistoryReady);
            program.Uniform("RightEyeHistoryReady", data.RightEyeHistoryReady);
            return;
        }

        XRCamera? camera = pipeline.RenderState.RenderingCamera ?? pipeline.LastRenderingCamera;
        if (camera is null)
            throw new InvalidOperationException("Advanced transparent velocity requires an active camera for its output.");

        XRCamera? rightCamera = pipeline.RenderState.StereoRightEyeCamera;
        var current = camera.ViewProjectionMatrixUnjittered;
        var rightCurrent = rightCamera?.ViewProjectionMatrixUnjittered ?? current;
        bool leftReady = camera.HasPreviousViewProjectionMatrix;
        bool rightReady = rightCamera?.HasPreviousViewProjectionMatrix == true;
        var previous = leftReady ? camera.PreviousViewProjectionMatrixUnjittered : current;
        var rightPrevious = rightReady
            ? rightCamera!.PreviousViewProjectionMatrixUnjittered : rightCurrent;
        if (pipeline.RenderState.FrameViewSet is { } frameViews)
        {
            leftReady = false;
            rightReady = false;
            previous = current;
            rightPrevious = rightCurrent;
            if (RenderFrameViewMatch.TryGetExactEyeView(in frameViews, camera, rightEye: false, out RenderFrameViewDescriptor leftView))
            {
                var projection = leftView.ProjectionMatrixUnjittered == default
                    ? leftView.ProjectionMatrix : leftView.ProjectionMatrixUnjittered;
                current = leftView.ViewMatrix * projection;
                leftReady = leftView.HasValidTemporalHistory &&
                    leftView.PreviousViewProjectionMatrixUnjittered != default;
                previous = leftReady ? leftView.PreviousViewProjectionMatrixUnjittered : current;
            }
            if (RenderFrameViewMatch.TryGetExactEyeView(in frameViews, rightCamera, rightEye: true, out RenderFrameViewDescriptor rightView))
            {
                var projection = rightView.ProjectionMatrixUnjittered == default
                    ? rightView.ProjectionMatrix : rightView.ProjectionMatrixUnjittered;
                rightCurrent = rightView.ViewMatrix * projection;
                rightReady = rightView.HasValidTemporalHistory &&
                    rightView.PreviousViewProjectionMatrixUnjittered != default;
                rightPrevious = rightReady ? rightView.PreviousViewProjectionMatrixUnjittered : rightCurrent;
            }
        }
        program.Uniform("CurrViewProjection", current);
        program.Uniform("PrevViewProjection", previous);
        program.Uniform("RightEyeCurrViewProjection", rightCurrent);
        program.Uniform("RightEyePrevViewProjection", rightPrevious);
        program.Uniform("LeftEyeHistoryReady", leftReady);
        program.Uniform("RightEyeHistoryReady", rightReady);
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
