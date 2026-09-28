using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Snapshots an engine framebuffer into browser attachment intent during resource preparation.</summary>
public static class BrowserFrameBufferAdapter
{
    /// <summary>
    /// The resolver returns a ready view for the exact target, mip and layer. Policies supply
    /// load/store/clear and optional resolve intent, which XRFrameBuffer itself does not own.
    /// </summary>
    public static BrowserFrameBufferPlan FromXRFrameBuffer(XRFrameBuffer frameBuffer,
        Func<IFrameBufferAttachement, int, int, int> resolveView,
        Func<int, int, BrowserColorAttachmentPlan> colorPolicy,
        Func<int, EFrameBufferAttachment, BrowserDepthStencilAttachmentPlan> depthStencilPolicy)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        ArgumentNullException.ThrowIfNull(resolveView);
        ArgumentNullException.ThrowIfNull(colorPolicy);
        ArgumentNullException.ThrowIfNull(depthStencilPolicy);
        if (frameBuffer.ForceOvrMultiview)
            throw new NotSupportedException("Browser framebuffer export requires a single view.");
        var targets = frameBuffer.Targets;
        if (targets is null || targets.Length == 0)
            throw new ArgumentException("Framebuffer has no declared attachments.", nameof(frameBuffer));
        BrowserColorAttachmentPlan?[] colors = new BrowserColorAttachmentPlan?[8];
        BrowserDepthStencilAttachmentPlan? depthStencil = null;
        int colorCount = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, mip, layer) = targets[i];
            if (mip < 0 || layer < -1) throw new NotSupportedException("Framebuffer subresource range is invalid.");
            int handle = resolveView(target, mip, layer);
            _ = BrowserResourceHandle.FromPacked(handle);
            int slot = attachment switch
            {
                EFrameBufferAttachment.ColorAttachment0 => 0,
                EFrameBufferAttachment.ColorAttachment1 => 1,
                EFrameBufferAttachment.ColorAttachment2 => 2,
                EFrameBufferAttachment.ColorAttachment3 => 3,
                EFrameBufferAttachment.ColorAttachment4 => 4,
                EFrameBufferAttachment.ColorAttachment5 => 5,
                EFrameBufferAttachment.ColorAttachment6 => 6,
                EFrameBufferAttachment.ColorAttachment7 => 7,
                _ => -1,
            };
            if (slot >= 0)
            {
                if (colors[slot] is not null) throw new ArgumentException("Framebuffer contains duplicate color slots.");
                BrowserColorAttachmentPlan policy = colorPolicy(slot, handle);
                if (policy is null || policy.ViewHandle != handle) throw new ArgumentException("Color policy changed the resolved attachment identity.");
                colors[slot] = policy;
                colorCount = Math.Max(colorCount, slot + 1);
                continue;
            }
            if (attachment is not (EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment))
                throw new NotSupportedException($"Framebuffer attachment {attachment} is not supported by the browser profile.");
            if (depthStencil is not null) throw new NotSupportedException("Separate depth and stencil views must be combined into one depth/stencil attachment.");
            depthStencil = depthStencilPolicy(handle, attachment);
            bool hasDepth = attachment != EFrameBufferAttachment.StencilAttachment;
            bool hasStencil = attachment != EFrameBufferAttachment.DepthAttachment;
            if (depthStencil is null || depthStencil.ViewHandle != handle || depthStencil.HasDepth != hasDepth || depthStencil.HasStencil != hasStencil)
                throw new ArgumentException("Depth/stencil policy changed the resolved attachment identity or aspects.");
        }
        if (!ReferenceEquals(targets, frameBuffer.Targets)) throw new InvalidOperationException("Framebuffer changed during browser resource preparation.");
        return new BrowserFrameBufferPlan(colors.AsSpan(0, colorCount), depthStencil);
    }
}
