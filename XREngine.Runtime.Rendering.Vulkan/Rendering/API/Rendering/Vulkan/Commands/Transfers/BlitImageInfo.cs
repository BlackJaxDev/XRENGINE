using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly struct BlitImageInfo(
    Image image,
    Format format,
    ImageAspectFlags aspectMask,
    uint baseArrayLayer,
    uint layerCount,
    uint mipLevel,
    Extent2D extent,
    ImageLayout preferredLayout,
    PipelineStageFlags stageMask,
    AccessFlags accessMask,
    IVkImageDescriptorSource? descriptorSource = null,
    VkRenderBuffer? renderBufferSource = null,
    SampleCountFlags samples = default,
    ImageUsageFlags usage = default)
{
    public Image Image { get; } = image;
    public Format Format { get; } = format;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public uint BaseArrayLayer { get; } = baseArrayLayer;
    public uint LayerCount { get; } = layerCount;
    public uint MipLevel { get; } = mipLevel;
    public Extent2D Extent { get; } = extent;
    public ImageLayout PreferredLayout { get; } = preferredLayout;
    public PipelineStageFlags StageMask { get; } = stageMask;
    public AccessFlags AccessMask { get; } = accessMask;
    public IVkImageDescriptorSource? DescriptorSource { get; } = descriptorSource;
    public VkRenderBuffer? RenderBufferSource { get; } = renderBufferSource;
    public SampleCountFlags Samples { get; } = samples != default
        ? samples
        : descriptorSource?.DescriptorSamples
            ?? renderBufferSource?.Samples
            ?? SampleCountFlags.Count1Bit;
    /// <summary>Usage of the exact native image, including planner-owned images without a wrapper.</summary>
    public ImageUsageFlags Usage { get; } = usage != default
        ? usage
        : descriptorSource?.DescriptorUsage
            ?? renderBufferSource?.PhysicalGroup?.Usage
            ?? ImageUsageFlags.None;
    public bool IsValid => Image.Handle != 0;

    public BlitImageInfo WithResolvedState(
        Image resolvedImage,
        ImageLayout resolvedLayout,
        Extent2D resolvedExtent)
        => new(
            resolvedImage,
            Format,
            AspectMask,
            BaseArrayLayer,
            LayerCount,
            MipLevel,
            resolvedExtent,
            resolvedLayout,
            StageMask,
            AccessMask,
            DescriptorSource,
            RenderBufferSource,
            Samples,
            Usage);

    public BlitImageInfo WithLayerCount(uint resolvedLayerCount)
        => new(
            Image,
            Format,
            AspectMask,
            BaseArrayLayer,
            Math.Max(resolvedLayerCount, 1u),
            MipLevel,
            Extent,
            PreferredLayout,
            StageMask,
            AccessMask,
            DescriptorSource,
            RenderBufferSource,
            Samples,
            Usage);
}
