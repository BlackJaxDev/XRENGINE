namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct ResourceAllocationSignatureBreakdown(
    int AllocationDescriptors,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    int PhysicalUsage,
    bool SupportsTransformFeedback)
{
    public override string ToString()
        => $"allocDescriptors=0x{AllocationDescriptors:X8} dims={DisplayWidth}x{DisplayHeight}/{InternalWidth}x{InternalHeight} " +
           $"physicalUsage=0x{PhysicalUsage:X8} xfb={SupportsTransformFeedback}";
}
