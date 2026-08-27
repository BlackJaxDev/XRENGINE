namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free diagnostic snapshot of the protected foreground staging lane.
/// </summary>
internal readonly record struct VulkanForegroundStagingReserveSnapshot(
    int ConfiguredCount,
    int TotalCount,
    int IdleCount,
    int InUseCount,
    int RetiringCount,
    int DistinctBufferCount,
    int DistinctGenerationCount,
    ulong IdentitySignature);
