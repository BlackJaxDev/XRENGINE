using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.PostProcessing;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelinePurposeTests
{
    [Test]
    [NonParallelizable]
    public void Factory_RoutesDesktopCaptureAndOpenXrToTheirOwnedArchitectures()
    {
        AdvancedRenderPipelineCapabilities capabilities =
            AdvancedRenderPipelineCapabilityTests.SupportedCapabilities;

        RenderPipeline desktop = EngineRenderingSettingsApplication.NewRenderPipeline(
            RenderPipelineRequest.DesktopScene(),
            EAdvancedRenderPipelineMode.Available,
            capabilities,
            ERvcPipelineMode.Full,
            useDebugOpaquePipeline: false);
        desktop.ShouldBeOfType<AdvancedRenderPipeline>();

        RenderPipeline capture = EngineRenderingSettingsApplication.NewRenderPipeline(
            RenderPipelineRequest.OffscreenCapture(),
            EAdvancedRenderPipelineMode.Available,
            capabilities,
            ERvcPipelineMode.Full,
            useDebugOpaquePipeline: true);
        capture.ShouldBeOfType<AdvancedRenderPipeline>();

        RvcRenderPipeline monoEye =
            EngineRenderingSettingsApplication.NewRenderPipeline(
                    RenderPipelineRequest.OpenXrEye(stereo: false),
                    EAdvancedRenderPipelineMode.Available,
                    capabilities,
                    ERvcPipelineMode.Off,
                    useDebugOpaquePipeline: true)
                .ShouldBeOfType<RvcRenderPipeline>();
        monoEye.Stereo.ShouldBeFalse();
        monoEye.RvcPipelineMode.ShouldBe(ERvcPipelineMode.Off);

        RvcRenderPipeline stereoEye =
            EngineRenderingSettingsApplication.NewRenderPipeline(
                    RenderPipelineRequest.OpenXrEye(stereo: true),
                    EAdvancedRenderPipelineMode.Available,
                    capabilities,
                    ERvcPipelineMode.Full,
                    useDebugOpaquePipeline: false)
                .ShouldBeOfType<RvcRenderPipeline>();
        stereoEye.Stereo.ShouldBeTrue();
        stereoEye.RvcPipelineMode.ShouldBe(ERvcPipelineMode.Full);
    }

    [Test]
    [NonParallelizable]
    public void OpenXrPipelineCreation_DoesNotCloneAdvancedDesktopArchitecture()
    {
        ERvcPipelineMode previousMode =
            RuntimeEngine.Rendering.Settings.RvcPipelineMode;

        try
        {
            RuntimeEngine.Rendering.Settings.RvcPipelineMode =
                ERvcPipelineMode.Off;
            EngineRenderingSettingsApplication.InitializeSettingsApplicationBoundary();

            AdvancedRenderPipeline desktop = new(stereo: false)
            {
                GlobalIlluminationMode =
                    EGlobalIlluminationMode.RadianceCascades,
                ForwardDepthPrePassEnabled = false,
                ForwardPrePassSharesGBufferTargets = false,
                ForwardDepthNormalPrePassResolution =
                    EDepthNormalPrePassResolution.Half,
            };

            RvcRenderPipeline eye =
                OpenXRAPI.CreateOpenXrPipeline(desktop, stereo: true)
                    .ShouldBeOfType<RvcRenderPipeline>();

            eye.Stereo.ShouldBeTrue();
            eye.RvcPipelineMode.ShouldBe(ERvcPipelineMode.Off);
            eye.GlobalIlluminationMode.ShouldBe(
                EGlobalIlluminationMode.RadianceCascades);
            eye.ForwardDepthPrePassEnabled.ShouldBeFalse();
            eye.ForwardPrePassSharesGBufferTargets.ShouldBeFalse();
            eye.ForwardDepthNormalPrePassResolution.ShouldBe(
                EDepthNormalPrePassResolution.Half);
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.RvcPipelineMode = previousMode;
        }
    }

    [Test]
    public void ScenePipelines_ExposeCompatibleTemporalFroxelAndPostProcessSchemas()
    {
        RenderPipeline[] pipelines =
        [
            new DefaultRenderPipeline(stereo: false),
            new AdvancedRenderPipeline(stereo: false),
            new RvcRenderPipeline(stereo: true, ERvcPipelineMode.Off),
        ];

        foreach (RenderPipeline pipeline in pipelines)
            pipeline.ShouldBeAssignableTo<ISceneRenderPipelineFeatureProvider>();

        string[] referenceSchema = DescribeSchema(pipelines[0].PostProcessSchema);
        referenceSchema.ShouldContain(
            entry => entry.StartsWith("temporalAntiAliasing:", StringComparison.Ordinal));
        referenceSchema.ShouldContain(
            entry => entry.StartsWith("volumetricFog:", StringComparison.Ordinal));

        DescribeSchema(pipelines[1].PostProcessSchema).ShouldBe(referenceSchema);
        DescribeSchema(pipelines[2].PostProcessSchema).ShouldBe(referenceSchema);
    }

    [Test]
    public void FeatureSynchronizer_CopiesTemporalAndFroxelCameraStateAcrossArchitectures()
    {
        DefaultRenderPipeline sourcePipeline = new(stereo: false);
        RvcRenderPipeline destinationPipeline =
            new(stereo: true, ERvcPipelineMode.Off);
        XRCamera sourceCamera = new();
        XRCamera destinationCamera = new();

        var sourceState =
            sourceCamera.PostProcessStates.GetOrCreateState(sourcePipeline);
        sourceState.GetStage("temporalAntiAliasing")!
            .SetValue("FeedbackMin", 0.42f);
        sourceState.GetStage("volumetricFog")!
            .SetValue("Enabled", true);

        RenderPipelineFeatureSynchronizer.TryCopyCameraPostProcessState(
                sourcePipeline,
                destinationPipeline,
                sourceCamera,
                destinationCamera)
            .ShouldBeTrue();

        var destinationState =
            destinationCamera.PostProcessStates.GetOrCreateState(
                destinationPipeline);
        destinationState.GetStage("temporalAntiAliasing")!
            .Values["FeedbackMin"].ShouldBe(0.42f);
        destinationState.GetStage("volumetricFog")!
            .Values["Enabled"].ShouldBe(true);
    }

    [Test]
    public void Viewport_PreservesPurposeWhilePipelineAssignmentTracksStereoTopology()
    {
        XRViewport viewport = new(null)
        {
            PipelineRequest = RenderPipelineRequest.OpenXrEye(stereo: false),
        };

        viewport.RenderPipeline =
            new RvcRenderPipeline(stereo: true, ERvcPipelineMode.Off);

        viewport.PipelineRequest.Purpose.ShouldBe(
            ERenderPipelinePurpose.OpenXrEye);
        viewport.PipelineRequest.Stereo.ShouldBeTrue();
    }

    private static string[] DescribeSchema(
        RenderPipelinePostProcessSchema schema)
        => schema.StagesByKey
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
                $"{pair.Key}:{string.Join(',', pair.Value.Parameters.Select(parameter => $"{parameter.Name}/{parameter.Kind}/{parameter.UniformName}"))}")
            .ToArray();
}
