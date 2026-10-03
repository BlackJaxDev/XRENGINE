using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private const ulong WebBloomFeatureBit = 1UL << 58;
    private const ulong WebMotionBlurFeatureBit = 1UL << 59;
    private const ulong WebDepthOfFieldFeatureBit = 1UL << 60;
    private const ulong WebAutoExposureFeatureBit = 1UL << 61;

    private ulong BuildAdvancedWebOutputFeatureMask(XRRenderPipelineInstance instance, XRViewport? viewport)
    {
        if (!WebPipelineRasterProgram.IsActive)
            return 0;
        XRCamera? camera = viewport?.ActiveCamera ?? instance.RenderState.SceneCamera ?? instance.LastSceneCamera;
        PipelinePostProcessState? state = camera?.GetPostProcessState(this);
        ulong features = AdvancedVisibilitySampleContract.PackedUInt16FeatureBit;
        if (GetSettings<BloomSettings>(state) is not { Enabled: false })
            features |= WebBloomFeatureBit;
        if (GetSettings<MotionBlurSettings>(state) is { Enabled: true })
            features |= WebMotionBlurFeatureBit;
        if (GetSettings<DepthOfFieldSettings>(state) is { Enabled: true })
            features |= WebDepthOfFieldFeatureBit;
        if (GetSettings<ColorGradingSettings>(state) is { RequiresAutoExposure: true })
            features |= WebAutoExposureFeatureBit;
        return features;
    }

    private static bool HasAdvancedWebFeature(RenderPipelineResourceProfile profile, ulong feature)
        => (profile.FeatureMask & feature) != 0;

    private void DescribeAdvancedWebPostRequirements(RenderPipelineRequirements requirements)
    {
        requirements.RequireOperation("fullscreen-quad");
        requirements.SupportedAntiAliasingModes.Add(EAntiAliasingMode.Fxaa);
        requirements.SupportedAntiAliasingModes.Add(EAntiAliasingMode.Smaa);
        RenderPipelineResourceProfile profile = requirements.OutputProfile;
        if (Stereo || profile.Stereo || profile.ViewCount != 1 ||
            profile.AntiAliasingMode == EAntiAliasingMode.Msaa && profile.MsaaSampleCount is not (1 or 4))
            requirements.Diagnostics.Add("Advanced WebGPU visibility requires one mono view and either one sample or the exact packed16 four-sample family.");
        if (OffscreenProfile is not null)
            requirements.Diagnostics.Add("The selected Advanced offscreen export profile has no installed WebGPU export route.");
        if (GlobalIlluminationMode is not (EGlobalIlluminationMode.None or EGlobalIlluminationMode.LightProbesAndIbl))
            requirements.Diagnostics.Add("The selected Advanced global illumination provider has no WebGPU execution route.");

        PipelinePostProcessState state = requirements.PostProcessState;
        if (GetAdvancedWebPostProcessRejection(state) is { } rejection)
            requirements.Diagnostics.Add(rejection);
        if (GetSettings<BloomSettings>(state) is { Enabled: true })
        {
            requirements.RequireRasterProgram("bloom-copy");
            requirements.RequireRasterProgram("bloom-downsample");
            requirements.RequireRasterProgram("bloom-upsample");
        }
        if (GetSettings<MotionBlurSettings>(state) is { Enabled: true })
            requirements.RequireRasterProgram("advanced::motion-blur");
        if (GetSettings<DepthOfFieldSettings>(state) is { Enabled: true })
            requirements.RequireRasterProgram("advanced::depth-of-field");
        if (GetSettings<ColorGradingSettings>(state) is { RequiresAutoExposure: true })
            requirements.RequireComputeProgram("advanced::auto-exposure");
        if (profile.AntiAliasingMode == EAntiAliasingMode.Fxaa)
            requirements.RequireRasterProgram("advanced::fxaa");
        if (profile.AntiAliasingMode == EAntiAliasingMode.Smaa)
        {
            requirements.RequireRasterProgram("advanced::smaa-edge");
            requirements.RequireRasterProgram("advanced::smaa-blend");
            requirements.RequireRasterProgram("advanced::smaa-neighborhood");
        }
        if (AllowsLateTransparency)
        {
            requirements.RequireRasterProgram("advanced::transparent-resolve");
            requirements.ScenePasses.Add((int)EDefaultRenderPass.TransparentForward);
            requirements.ScenePasses.Add((int)EDefaultRenderPass.WeightedBlendedOitForward);
            requirements.ScenePasses.Add((int)EDefaultRenderPass.OnTopForward);
            requirements.ScenePasses.Add((int)EDefaultRenderPass.PostMotionBlurForward);
            requirements.ScenePasses.Add((int)EDefaultRenderPass.PostDepthOfFieldForward);
            requirements.ScenePasses.Add((int)EDefaultRenderPass.PostBloomForward);
        }
    }

    private static string? GetAdvancedWebPostProcessRejection(PipelinePostProcessState? state)
    {
        if (GetSettings<GpuBvhDebugSettings>(state) is { Enabled: true } or { MeshletDebugDisplayEnabled: true } or { FullOverdrawEnabled: true })
            return "Selected Advanced GPU debug visualization has no cooked WebGPU output pass.";
        return null;
    }

    private void ValidateAdvancedWebPostProcess()
    {
        if (!WebPipelineRasterProgram.IsActive)
            return;
        if (GetAdvancedWebPostProcessRejection(ResolveCurrentSettingsCamera()?.GetPostProcessState(this)) is { } reason)
            throw new NotSupportedException($"WebGPU.AdvancedPipeline.EffectUnsupported: {reason}");
        if (ShouldRunAdvancedAtmosphericScattering() || ShouldRunAdvancedVolumetricFog())
            throw new NotSupportedException("WebGPU.AdvancedPipeline.EffectUnsupported: active atmospheric scattering or volumetric fog has no cooked WebGPU output pass.");
        if (RuntimeNeedsTemporalAaVelocityBuffer || RuntimeNeedsTsrUpscale)
            throw new NotSupportedException("WebGPU.AdvancedPipeline.TemporalUnsupported: the selected temporal output has no cooked WebGPU history producer.");
    }
}
