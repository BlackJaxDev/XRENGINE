namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Cold-path description of the currently published native Vulkan allocation
/// backing an engine buffer. The generation comes from the resource lifetime
/// ledger; no diagnostic counter is synthesized.
/// </summary>
public readonly record struct VulkanNativeBufferDiagnosticDescription(
    ulong BufferHandle,
    ulong AllocatedByteSize,
    ulong PublishedGeneration,
    bool IsGenerated,
    bool IsDeviceOperational,
    uint MemoryTypeIndex = uint.MaxValue,
    uint MemoryHeapIndex = uint.MaxValue,
    ulong MemoryHeapSize = 0,
    string MemoryTypeFlags = "Unknown",
    string MemoryHeapFlags = "Unknown",
    bool IsMapped = false,
    bool IsHostCoherent = false,
    bool IsDeviceLocal = false,
    string AllocationOwnership = "Unknown");
