namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private AdvancedOffscreenProfile? _offscreenProfile;
    private EAdvancedStageFamilyExecutionProfile _stageFamilyExecutionProfile;

    /// <summary>
    /// Stereo execution topology derived from the configured stage family and
    /// renderer. It cannot disagree with the immutable layered resource profile.
    /// </summary>
    public EAdvancedStereoMode StereoMode
        => _stageFamilyExecutionProfile == EAdvancedStageFamilyExecutionProfile.OpenXrTwoPassEye
            ? EAdvancedStereoMode.RvcTwoPass
            : !Stereo ? EAdvancedStereoMode.Mono
            : (RuntimeRenderingHostServices.FrameTiming.CurrentRenderer?.GetAdvancedRenderPipelineCapabilities().Backend ?? CapabilityResult.Capabilities.Backend) == RuntimeGraphicsApiKind.OpenGL
                ? EAdvancedStereoMode.OpenGlSinglePassStereo : EAdvancedStereoMode.VulkanMultiview;

    /// <summary>
    /// Optional offscreen capability profile if this pipeline instance drives a secondary view.
    /// </summary>
    public AdvancedOffscreenProfile? OffscreenProfile
    {
        get => _offscreenProfile;
        set
        {
            if (!SetField(ref _offscreenProfile, value))
                return;
            InvalidateStereoResourceProfile();
            RebuildCommandChain();
        }
    }

    private void InvalidateStereoResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("StereoProfileChanged");

    // Secondary views deliberately opt in to expensive late work.  Keep this
    // decision here rather than scattering ViewKind checks through the command
    // graph: callers can create a custom profile without acquiring a new kind.
    private bool AllowsLateTransparency
        => OffscreenProfile?.EnableLateTransparency != false;

    private bool AllowsPostProcessing
        => OffscreenProfile?.EnablePostProcessing != false;

    private bool AllowsTemporalHistory
        => AllowsPostProcessing &&
           OffscreenProfile?.EnableTemporalHistory != false;

    private bool AllowsBloomAndDepthOfField
        => AllowsPostProcessing &&
           OffscreenProfile?.EnableBloomAndDoF != false;

    private bool AllowsScreenSpaceUi
        => _stageFamilyExecutionProfile == EAdvancedStageFamilyExecutionProfile.DesktopPresent &&
           OffscreenProfile is null;

    private bool UsesMinimalVisibilityOutput
        => OffscreenProfile?.IsMinimalVisibilityOutput == true;

    internal bool IsMinimalVisibilityOutput
        => UsesMinimalVisibilityOutput;

    /// <summary>
    /// Resolves the geometry consumers needed before this profile may author
    /// its first native producer. Minimal depth and visibility exports must not
    /// acquire reconstruction, lighting, shadow, or probe dependencies.
    /// </summary>
    internal EAdvancedPreparationConsumer RequiredPreparationConsumers
        => UsesMinimalVisibilityOutput
            ? EAdvancedPreparationConsumer.Visibility |
              EAdvancedPreparationConsumer.Depth |
              EAdvancedPreparationConsumer.Capture
            : EAdvancedPreparationConsumer.Visibility |
              EAdvancedPreparationConsumer.Depth |
              EAdvancedPreparationConsumer.Velocity |
              EAdvancedPreparationConsumer.MaterialReconstruction |
              EAdvancedPreparationConsumer.DirectionalShadow |
              EAdvancedPreparationConsumer.PointShadow |
              EAdvancedPreparationConsumer.SpotShadow |
              EAdvancedPreparationConsumer.Probe |
              EAdvancedPreparationConsumer.Capture;

    private bool IncludesStage(EAdvancedRenderStage stage)
        => !UsesMinimalVisibilityOutput || stage is
            EAdvancedRenderStage.FrameBegin or
            EAdvancedRenderStage.Deformation or
            EAdvancedRenderStage.VisibilityPreparation or
            EAdvancedRenderStage.VisibilityRaster or
            EAdvancedRenderStage.DepthPyramidAndLateVisibility or
            EAdvancedRenderStage.Output;

    // The two-pass family is rebound to the physical RVC eye instance before
    // execution. Its persistent resources and frame-view-history identity are
    // consequently per eye, even though the command definition is cached.
    // Keep all temporal begin/accumulate/pop/commit predicates together.
    private bool AllowsPostAntiAliasing
        => true;
}
