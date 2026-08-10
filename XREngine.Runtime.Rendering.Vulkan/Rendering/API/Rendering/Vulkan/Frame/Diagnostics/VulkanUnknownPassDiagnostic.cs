namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free payload for a render operation whose pass is absent from
/// the frozen graph plan used by primary command recording.
/// </summary>
internal readonly record struct VulkanUnknownPassDiagnostic(
    int PassIndex,
    string PassName,
    EVulkanFrameOpContextKind ContextKind,
    int PipelineIdentity,
    int ViewportIdentity,
    int SchedulingIdentity,
    bool ContextMetadataContainsPass,
    int ContextPassCount,
    ulong FrozenPlanRevision,
    ulong FrozenPlanGeneration,
    int FrozenPassCount)
{
    public override string ToString()
        => $"pass={PassIndex} name='{PassName}' " +
           $"op-context=[kind={ContextKind} pipe={PipelineIdentity} viewport={ViewportIdentity} scheduling={SchedulingIdentity} metadataHasPass={ContextMetadataContainsPass} metadataPasses={ContextPassCount}] " +
           $"frozen-plan=[revision={FrozenPlanRevision} generation={FrozenPlanGeneration} passes={FrozenPassCount}]";
}
