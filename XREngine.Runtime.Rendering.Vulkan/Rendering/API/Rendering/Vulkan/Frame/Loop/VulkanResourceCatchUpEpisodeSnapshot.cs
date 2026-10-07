namespace XREngine.Rendering.Vulkan;

/// <summary>Stores the first and latest blockers from a resource catch-up episode.</summary>
internal readonly record struct VulkanResourceCatchUpEpisodeSnapshot(
    bool HasEpisode,
    bool IsActive,
    ulong FirstFrameId,
    ulong LastBlockedFrameId,
    ulong BlockerCount,
    ulong RecoveryFrameId,
    VulkanResourceCatchUpBlockerSnapshot FirstBlocker,
    VulkanResourceCatchUpBlockerSnapshot LatestBlocker);
