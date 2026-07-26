namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Specifies the source of a Vulkan descriptor mapping in the EXT descriptor heap extension.
/// </summary>
internal enum VulkanDescriptorMappingSourceEXT : uint
{
    /// <summary>
    /// The descriptor mapping comes from a heap with a constant offset.
    /// </summary>
    HeapWithConstantOffset = 0,
    /// <summary>
    /// The descriptor mapping comes from a heap with a push index.
    /// </summary>
    HeapWithPushIndex = 1,
    /// <summary>
    /// The descriptor mapping comes from a heap with an indirect index.
    /// </summary>
    HeapWithIndirectIndex = 2,
    /// <summary>
    /// The descriptor mapping comes from a heap with an indirect index array.
    /// </summary>
    HeapWithIndirectIndexArray = 3,
    /// <summary>
    /// The descriptor mapping comes from resource heap data.
    /// </summary>
    ResourceHeapData = 4,
    /// <summary>
    /// The descriptor mapping comes from push data.
    /// </summary>
    PushData = 5,
    /// <summary>
    /// The descriptor mapping comes from a push address.
    /// </summary>
    PushAddress = 6,
    /// <summary>
    /// The descriptor mapping comes from an indirect address.
    /// </summary>
    IndirectAddress = 7,
    /// <summary>
    /// The descriptor mapping comes from a heap with a shader record index.
    /// </summary>
    HeapWithShaderRecordIndex = 8,
    /// <summary>
    /// The descriptor mapping comes from shader record data.
    /// </summary>
    ShaderRecordData = 9,
    /// <summary>
    /// The descriptor mapping comes from a shader record address.
    /// </summary>
    ShaderRecordAddress = 10,
}
