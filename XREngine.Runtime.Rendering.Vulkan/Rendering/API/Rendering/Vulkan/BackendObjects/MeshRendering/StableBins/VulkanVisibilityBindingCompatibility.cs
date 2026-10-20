namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact canonical atlas and vertex source for one visibility bin. Geometry
/// records can differ while these native bindings stay the same.
/// </summary>
/// <remarks>
/// A deformed bin has an empty <see cref="VertexSource"/>: the deformation
/// output buffer is a frame binding of the stream, not part of the bin identity.
/// </remarks>
internal readonly record struct VulkanVisibilityBindingCompatibility(
    XREngine.Rendering.Commands.AdvancedGpuHandle Geometry,
    EAdvancedGeometryProducer Producer,
    uint RasterStateClass,
    EAdvancedMaterialCoverageMode Coverage,
    uint CullMode,
    uint PrimitiveTopology,
    bool UsesDeformedVertexSource,
    VulkanFrameDataSlice IndexSlice,
    VulkanVisibilityPreparedVertexSource VertexSource)
{
    /// <summary>True for a canonical visibility bin; ordinary bins keep the default value.</summary>
    internal bool IsVisibilityBin => IndexSlice.IsValid;
}
