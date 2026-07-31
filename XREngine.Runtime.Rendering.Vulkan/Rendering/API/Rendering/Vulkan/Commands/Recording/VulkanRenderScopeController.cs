using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.Commands;

/// <summary>
/// Owns the mutable render-scope metadata for one primary command-buffer recording.
/// The instance is reused through command-recording scratch to keep the hot path allocation-free.
/// </summary>
internal sealed class VulkanRenderScopeController
{
    public bool IsActive { get; private set; }
    public bool UsesDynamicRendering { get; set; }
    public XRFrameBuffer? Target { get; set; }
    public RenderPass RenderPass { get; set; }
    public Framebuffer Framebuffer { get; set; }
    public DynamicRenderingFormatSignature DynamicRenderingFormats { get; set; }
    public FrameBufferAttachmentSignature[]? AttachmentSignature { get; set; }
    public Rect2D RenderArea { get; set; }
    public bool DepthStencilReadOnly { get; set; }
    public DynamicRenderingLocalReadSignature LocalReadSignature { get; set; }
    public RenderingFlags InheritanceRenderingFlags { get; set; }

    public void Activate(
        XRFrameBuffer? target,
        bool usesDynamicRendering,
        RenderPass renderPass,
        Framebuffer framebuffer,
        in DynamicRenderingFormatSignature dynamicRenderingFormats,
        FrameBufferAttachmentSignature[]? attachmentSignature,
        in Rect2D renderArea,
        bool depthStencilReadOnly,
        DynamicRenderingLocalReadSignature localReadSignature = default,
        RenderingFlags inheritanceRenderingFlags = 0)
    {
        Target = target;
        UsesDynamicRendering = usesDynamicRendering;
        RenderPass = renderPass;
        Framebuffer = framebuffer;
        DynamicRenderingFormats = dynamicRenderingFormats;
        AttachmentSignature = attachmentSignature;
        RenderArea = renderArea;
        DepthStencilReadOnly = depthStencilReadOnly;
        LocalReadSignature = localReadSignature;
        InheritanceRenderingFlags = inheritanceRenderingFlags;
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
        UsesDynamicRendering = false;
        Target = null;
        RenderPass = default;
        Framebuffer = default;
        DynamicRenderingFormats = default;
        AttachmentSignature = null;
        RenderArea = default;
        DepthStencilReadOnly = false;
        LocalReadSignature = default;
        InheritanceRenderingFlags = 0;
    }

    public bool MatchesTarget(XRFrameBuffer? target)
        => IsActive && ReferenceEquals(Target, target);

    public bool ShouldPreserveForContextChange(
        bool incomingTargetsSwapchain,
        XRFrameBuffer? incomingTarget,
        int incomingPassIndex,
        bool hasInlineQuery,
        int incomingSchedulingIdentity,
        int activePassIndex,
        int activeSchedulingIdentity,
        bool queryScopeCompatible)
    {
        if (!IsActive || incomingPassIndex != activePassIndex)
            return false;

        if (Target is null && incomingTargetsSwapchain)
            return true;

        return hasInlineQuery &&
            ReferenceEquals(Target, incomingTarget) &&
            incomingSchedulingIdentity == activeSchedulingIdentity &&
            queryScopeCompatible;
    }
}
