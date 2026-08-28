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
    bool DepthStencilReadOnly)
{
    internal bool IsValid
        => Target is not null && NativeTarget.IsComplete &&
           RasterizationSamples != 0 &&
           (UsesDynamicRendering
               ? DynamicRenderingFormats.ColorAttachmentCount == 3u
               : RenderPass.Handle != 0);
}
