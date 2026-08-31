using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Declared graph-use intervals for transient-attachment candidate analysis.
/// This evidence does not establish native dependencies or initialization.
/// </summary>
internal sealed class VulkanTransientAttachmentLifetimeEvidence
{
    internal bool HasUse { get; private set; }
    internal bool GraphicsQueueOnly { get; private set; } = true;
    internal bool AttachmentOnly { get; private set; } = true;
    internal bool Imported { get; private set; }
    internal int FirstPassOrder { get; private set; } = int.MaxValue;
    internal int LastPassOrder { get; private set; } = -1;
    internal int FirstSubmissionIndex { get; private set; } = int.MaxValue;
    internal int LastSubmissionIndex { get; private set; } = -1;

    internal bool IsGraphicsQueueCandidate
        => HasUse &&
           GraphicsQueueOnly &&
           !Imported &&
           FirstSubmissionIndex >= 0 &&
           LastSubmissionIndex >= FirstSubmissionIndex;

    internal void Observe(
        int passOrder,
        int submissionIndex,
        bool graphicsQueue,
        ERenderPassResourceType resourceType,
        bool imported)
    {
        HasUse = true;
        FirstPassOrder = Math.Min(FirstPassOrder, passOrder);
        LastPassOrder = Math.Max(LastPassOrder, passOrder);
        FirstSubmissionIndex = Math.Min(FirstSubmissionIndex, submissionIndex);
        LastSubmissionIndex = Math.Max(LastSubmissionIndex, submissionIndex);
        GraphicsQueueOnly &= graphicsQueue && submissionIndex >= 0;
        AttachmentOnly &= resourceType is
            ERenderPassResourceType.ColorAttachment or
            ERenderPassResourceType.DepthAttachment or
            ERenderPassResourceType.StencilAttachment or
            ERenderPassResourceType.ResolveAttachment;
        Imported |= imported;
    }
}
