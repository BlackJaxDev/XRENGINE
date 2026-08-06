using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable logical identity for a frame-plan operation. This separates
/// operation planning from the physical <see cref="RecordedPacketKey"/>, which
/// is constructed only after native render-target, descriptor, and buffer state
/// have been prepared.
/// </summary>
internal readonly record struct FramePlanOperationKey(
    int SourceIndex,
    EVulkanPrimaryPlanNodeKind Kind,
    int PassIndex,
    EVulkanFrameOpContextKind ContextKind,
    int OutputTargetIdentity,
    int OutputFrameBufferIdentity,
    int PipelineIdentity,
    int ViewportIdentity,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    ulong ContextFingerprint,
    bool IsDynamicOverlay)
{
    internal static FramePlanOperationKey FromOperation(
        FrameOp operation,
        int sourceIndex,
        bool isDynamicOverlay)
    {
        ArgumentNullException.ThrowIfNull(operation);
        FrameOpContext context = operation.Context;
        return new FramePlanOperationKey(
            sourceIndex,
            operation.Kind,
            operation.PassIndex,
            context.ContextKind,
            context.OutputTargetIdentity,
            context.OutputFrameBufferIdentity,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.RecordingFingerprint,
            isDynamicOverlay);
    }
}
