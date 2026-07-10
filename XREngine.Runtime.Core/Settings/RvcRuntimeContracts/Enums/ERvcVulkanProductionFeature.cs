namespace XREngine;

/// <summary>
/// Vulkan production features supported by the engine.
/// </summary>
[Flags]
public enum ERvcVulkanProductionFeature
{
    /// <summary>
    /// No Vulkan production features enabled.
    /// </summary>
    None = 0,
    /// <summary>
    /// Multiview rendering support.
    /// </summary>
    Multiview = 1 << 0,
    /// <summary>
    /// Dynamic rendering support.
    /// </summary>
    DynamicRendering = 1 << 1,
    /// <summary>
    /// Synchronization2 support.
    /// </summary>
    Synchronization2 = 1 << 2,
    /// <summary>
    /// Descriptor indexing support.
    /// </summary>
    DescriptorIndexing = 1 << 3,
    /// <summary>
    /// Fragment shading rate support.
    /// </summary>
    FragmentShadingRate = 1 << 4,
    /// <summary>
    /// Fragment density map support.
    /// </summary>
    FragmentDensityMap = 1 << 5,
    /// <summary>
    /// Mesh shader support.
    /// </summary>
    MeshShader = 1 << 6,
    /// <summary>
    /// Timeline semaphore support.
    /// </summary>
    TimelineSemaphore = 1 << 7,
}
