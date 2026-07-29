namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImageAllocationDebugInfo(
    ulong Handle,
    string Name,
    string Source,
    long SizeBytes,
    uint Width,
    uint Height,
    uint Depth,
    uint Layers,
    uint MipLevels,
    string Format,
    string Usage,
    string Samples,
    string AllocationClass,
    uint MemoryTypeIndex,
    string MemoryTypeFlags,
    uint MemoryHeapIndex,
    ulong MemoryHeapSize,
    string MemoryHeapFlags);
