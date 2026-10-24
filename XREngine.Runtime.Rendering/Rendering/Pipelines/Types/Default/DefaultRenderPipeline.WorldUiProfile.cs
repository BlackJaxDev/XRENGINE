using XREngine.Data.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    /// <summary>Admits only the mono color graph whose output has checked native links.</summary>
    internal bool SupportsRetainedWorldUiColorProfile(XRCamera camera)
    {
        bool bloomDisabled =
            camera.GetPostProcessStageState<BloomSettings>(this) is { } bloomStage &&
            bloomStage.TryGetBacking(out BloomSettings? bloom) &&
            bloom is { Enabled: false };
        bool motionEnabled =
            camera.GetPostProcessStageState<MotionBlurSettings>(this) is { } motionStage &&
            motionStage.TryGetBacking(out MotionBlurSettings? motion) &&
            motion is { Enabled: true };
        bool depthOfFieldEnabled =
            camera.GetPostProcessStageState<DepthOfFieldSettings>(this) is { } depthStage &&
            depthStage.TryGetBacking(out DepthOfFieldSettings? depthOfField) &&
            depthOfField is { Enabled: true };
        bool ambientOcclusionEnabled =
            camera.GetPostProcessStageState<AmbientOcclusionSettings>(this) is { } aoStage &&
            aoStage.TryGetBacking(out AmbientOcclusionSettings? ambientOcclusion) &&
            ambientOcclusion is { Enabled: true };
        PipelinePostProcessState? postProcess = camera.GetPostProcessState(this);
        if (Stereo || UseOpenXrVulkanDesktopStartupSafePath ||
            ExactTransparencyEnabled ||
            RuntimeEngine.StartupPresentationEnabled ||
            RuntimeEngine.VRState.IsInVR ||
            RuntimeEngine.Rendering.State.IsStereoPass ||
            RuntimeEngine.Rendering.State.IsSceneCapturePass ||
            RuntimeEngine.Rendering.State.IsLightProbePass ||
            RuntimeEngine.Rendering.State.IsMirrorPass ||
            RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode !=
                EOcclusionCullingMode.Disabled ||
            RuntimeEngine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling ||
            (camera.AntiAliasingModeOverride ??
                RuntimeEngine.EffectiveSettings.AntiAliasingMode) != EAntiAliasingMode.None ||
            RuntimeEngine.EffectiveSettings.EnableNvidiaDlss ||
            RuntimeEngine.EffectiveSettings.EnableIntelXess ||
            VendorUpscaleRuntime.IsDlssFrameGenerationRequested ||
            RuntimeEngine.Rendering.Settings.EnableIntelXessFrameGeneration ||
            RuntimeEnableVendorUpscale || RenderDiagnosticsFlags.BypassVendorUpscale ||
            ShouldGenerateVelocityBuffer() || motionEnabled ||
            depthOfFieldEnabled || !bloomDisabled ||
            ambientOcclusionEnabled || ShouldUseAmbientOcclusion() ||
            GetSettings<AtmosphericScatteringSettings>(postProcess) is { Enabled: true } ||
            GetSettings<VolumetricFogSettings>(postProcess) is { Enabled: true } ||
            GetSettings<LensDistortionSettings>(postProcess) is
                { Mode: not ELensDistortionMode.None } ||
            ShouldRunAtmosphericScattering() || ShouldRunVolumetricFog() ||
            HasFullPipelineDebugVisualization() ||
            RenderDiagnosticsFlags.OutputSourceFboOverride is not null ||
            GetSettings<ColorGradingSettings>(postProcess)
                is { RequiresAutoExposure: true })
            return false;
        return true;
    }
}
