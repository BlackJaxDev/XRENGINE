using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    public const string ExternalOutputResourceName = "$ExternalOutput";

    /// <summary>
    /// Captures the complete immutable resource/state profile for this pipeline.
    /// The inactive skeleton reserves no production stage capacity.
    /// </summary>
    internal AdvancedRenderResourceProfile CaptureAdvancedResourceProfile(
        in RenderPipelineResourceProfile targetProfile)
        => AdvancedRenderResourceProfile.CreateInactive(
            targetProfile,
            CapabilityResult.Capabilities);

    /// <summary>
    /// The frame skeleton owns no layout-affecting feature resources yet.
    /// Future slices add immutable profile bits alongside their first declarations.
    /// </summary>
    internal override ulong BuildResourceFeatureMaskForGenerationKey(
        XRRenderPipelineInstance instance,
        XRViewport? viewport)
        => 0UL;

    /// <summary>
    /// Until GPU-scene and visibility resources are implemented, the advanced
    /// layout contains only the externally owned output boundary.
    /// </summary>
    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineExternalTargetKind kind = builder.Profile.ExternalTargetKind;
        if (kind == RenderPipelineExternalTargetKind.None)
            return;

        ExternalRenderResourceOwnership ownership = kind switch
        {
            RenderPipelineExternalTargetKind.Window =>
                ExternalRenderResourceOwnership.Window,
            RenderPipelineExternalTargetKind.ExternalSwapchain =>
                ExternalRenderResourceOwnership.XrRuntime,
            _ => ExternalRenderResourceOwnership.Caller,
        };
        ExternalRenderResourceSynchronization synchronization = kind switch
        {
            RenderPipelineExternalTargetKind.Window =>
                ExternalRenderResourceSynchronization.FrameBoundary,
            RenderPipelineExternalTargetKind.ExternalSwapchain =>
                ExternalRenderResourceSynchronization.AcquireRelease,
            _ => ExternalRenderResourceSynchronization.CallerProvided,
        };

        builder.External(ExternalOutputResourceName)
            .Contract(
                ExternalRenderResourceKind.FrameBuffer,
                ownership,
                synchronization)
            .DebugLabel(kind.ToString())
            .Add();
    }
}
