using System;

namespace XREngine.Rendering.Vulkan;

internal readonly ref struct DynamicRenderingLocalReadPlan
{
    public DynamicRenderingLocalReadPlan(
        ReadOnlySpan<uint> colorAttachmentLocations,
        ReadOnlySpan<uint> colorInputAttachmentIndices,
        uint? depthInputAttachmentIndex = null,
        uint? stencilInputAttachmentIndex = null)
    {
        ColorAttachmentLocations = colorAttachmentLocations;
        ColorInputAttachmentIndices = colorInputAttachmentIndices;
        DepthInputAttachmentIndex = depthInputAttachmentIndex;
        StencilInputAttachmentIndex = stencilInputAttachmentIndex;
    }

    public ReadOnlySpan<uint> ColorAttachmentLocations { get; }
    public ReadOnlySpan<uint> ColorInputAttachmentIndices { get; }
    public uint? DepthInputAttachmentIndex { get; }
    public uint? StencilInputAttachmentIndex { get; }
    public bool Enabled =>
        ColorAttachmentLocations.Length > 0 ||
        ColorInputAttachmentIndices.Length > 0 ||
        DepthInputAttachmentIndex.HasValue ||
        StencilInputAttachmentIndex.HasValue;
}
