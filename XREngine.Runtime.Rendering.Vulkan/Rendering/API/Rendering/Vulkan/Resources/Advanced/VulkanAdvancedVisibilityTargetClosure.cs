using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact graphics-target compatibility frozen before command recording. The
/// managed target owns the attachments; the native snapshot detects ABA or
/// resource-generation changes before any compute or raster work is emitted.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityTargetClosure(
    XRFrameBuffer Target,
    VulkanRecordedRenderTargetSnapshot NativeTarget,
    bool UsesDynamicRendering,
    RenderPass RenderPass,
    DynamicRenderingFormatSignature DynamicRenderingFormats,
    SampleCountFlags RasterizationSamples,
    bool DepthStencilReadOnly,
    VulkanAdvancedVisibilityClearPolicy ClearPolicy,
    EVulkanAdvancedVisibilityTargetKind Kind = EVulkanAdvancedVisibilityTargetKind.Visibility)
{
    internal bool IsValid
        => Target is not null && NativeTarget.IsComplete &&
           RasterizationSamples != 0 &&
           (Kind == EVulkanAdvancedVisibilityTargetKind.DirectionalShadow
               ? UsesDynamicRendering &&
                 DynamicRenderingFormats.ColorAttachmentCount == 0u &&
                 DynamicRenderingFormats.DepthAttachmentFormat != Format.Undefined
               : UsesDynamicRendering
                   ? DynamicRenderingFormats.ColorAttachmentCount is 3u or 4u
                   : RenderPass.Handle != 0);

    /// <summary>
    /// Compares all fields. The managed target uses the default comparer. The render pass handle
    /// compares by value, so the check does not box it.
    /// </summary>
    public bool Equals(VulkanAdvancedVisibilityTargetClosure other)
        => EqualityComparer<XRFrameBuffer>.Default.Equals(Target, other.Target) &&
           NativeTarget.Equals(other.NativeTarget) &&
           UsesDynamicRendering == other.UsesDynamicRendering &&
           RenderPass.Handle == other.RenderPass.Handle &&
           DynamicRenderingFormats.Equals(other.DynamicRenderingFormats) &&
           RasterizationSamples == other.RasterizationSamples &&
           DepthStencilReadOnly == other.DepthStencilReadOnly &&
           ClearPolicy == other.ClearPolicy &&
           Kind == other.Kind;

    /// <summary>Combines a subset of the compared fields without boxing.</summary>
    public override int GetHashCode()
        => HashCode.Combine(
            Target,
            NativeTarget.FramebufferHandle,
            NativeTarget.FramebufferGeneration,
            RenderPass.Handle,
            DynamicRenderingFormats,
            (uint)RasterizationSamples,
            (int)Kind);
}
