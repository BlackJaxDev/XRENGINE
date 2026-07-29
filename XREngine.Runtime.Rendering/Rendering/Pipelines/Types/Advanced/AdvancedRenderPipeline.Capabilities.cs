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
        : this(stereo)
    {
        CapabilityResult = capabilityResult;
    }

    internal void RefreshCapabilityResult()
        => CapabilityResult = AdvancedRenderPipelineCapabilityResolver.ResolveCurrent(Stereo);

    internal void ApplyCapabilityResult(AdvancedRenderPipelineCapabilityResult capabilityResult)
        => CapabilityResult = capabilityResult;
}
