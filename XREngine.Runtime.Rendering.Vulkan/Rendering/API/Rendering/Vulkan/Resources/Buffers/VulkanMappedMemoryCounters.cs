namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free snapshot of mapping-boundary activity.</summary>
internal readonly record struct VulkanMappedMemoryCounters(
    long Reservations,
    long ReservedBytes,
    long FlushExpansionBytes,
    long InvalidateExpansionBytes,
    long Failures);
