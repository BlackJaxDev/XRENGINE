namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Specifies the types of SPIR-V resources in the EXT descriptor heap extension.
/// </summary>
[Flags]
internal enum VulkanSpirvResourceTypeFlagsEXT : uint
{
    /// <summary>
    /// The resource type is a sampler.
    /// </summary>
    Sampler = 1u << 0,
    /// <summary>
    /// The resource type is a sampled image.
    /// </summary>
    SampledImage = 1u << 1,
    /// <summary>
    /// The resource type is a read-only image.
    /// </summary>
    ReadOnlyImage = 1u << 2,
    /// <summary>
    /// The resource type is a read-write image.
    /// </summary>
    ReadWriteImage = 1u << 3,
    /// <summary>
    /// The resource type is a combined sampled image.
    /// </summary>
    CombinedSampledImage = 1u << 4,
    /// <summary>
    /// The resource type is a uniform buffer.
    /// </summary>
    UniformBuffer = 1u << 5,
    /// <summary>
    /// The resource type is a read-only storage buffer.
    /// </summary>
    ReadOnlyStorageBuffer = 1u << 6,
    /// <summary>
    /// The resource type is a read-write storage buffer.
    /// </summary>
    ReadWriteStorageBuffer = 1u << 7,
    /// <summary>
    /// The resource type is an acceleration structure.
    /// </summary>
    AccelerationStructure = 1u << 8,
    /// <summary>
    /// Represents all resource types.
    /// </summary>
    All = 0x7FFFFFFF,
}
