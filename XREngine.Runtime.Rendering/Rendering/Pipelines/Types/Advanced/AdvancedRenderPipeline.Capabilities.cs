namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline : IAdvancedRenderPipelineCapabilitySource
{
    private AdvancedRenderPipelineCapabilityResult _capabilityResult;

    /// <inheritdoc />
    public AdvancedRenderPipelineCapabilityResult CapabilityResult
    {
        get => _capabilityResult;
        private set => SetField(ref _capabilityResult, value);
    }

    internal AdvancedRenderPipeline(
        bool stereo,
        AdvancedRenderPipelineCapabilityResult capabilityResult)
        : this(
            stereo,
            (AdvancedRenderPipelineCapabilityResult?)capabilityResult,
            offscreenProfile: null)
    {
    }

    internal AdvancedRenderPipeline(
        bool stereo,
        AdvancedRenderPipelineCapabilityResult capabilityResult,
        AdvancedOffscreenProfile offscreenProfile)
        : this(
            stereo,
            (AdvancedRenderPipelineCapabilityResult?)capabilityResult,
            offscreenProfile)
    {
    }

    internal void RefreshCapabilityResult()
        => CapabilityResult = AdvancedRenderPipelineCapabilityResolver.ResolveCurrent(Stereo);

}
