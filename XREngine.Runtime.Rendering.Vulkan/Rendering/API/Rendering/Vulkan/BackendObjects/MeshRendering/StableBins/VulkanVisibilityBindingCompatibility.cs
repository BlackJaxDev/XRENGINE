namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact canonical atlas and prepared vertex source for one visibility bin.
/// Geometry records can differ while these native bindings stay the same.
/// </summary>
internal readonly record struct VulkanVisibilityBindingCompatibility(
    XREngine.Rendering.Commands.AdvancedGpuHandle Geometry,
    EAdvancedGeometryProducer Producer,
    uint RasterStateClass,
    EAdvancedMaterialCoverageMode Coverage,
    uint CullMode,
    uint PrimitiveTopology,
    bool UsesDeformedVertexSource,
    ulong SceneNativeGeneration,
    VulkanFrameDataSlice IndexSlice,
    VulkanVisibilityPreparedVertexSource VertexSource);
