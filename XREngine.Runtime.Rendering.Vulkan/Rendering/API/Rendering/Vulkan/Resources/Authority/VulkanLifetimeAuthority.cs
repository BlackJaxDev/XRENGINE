namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Couples resource generation tracking with deferred-retirement queue ownership.
/// </summary>
/// <remarks>
/// The two stores must remain paired: retirement admission mutates generation visibility and
/// queue completion releases the pins recorded by the lifetime tracker. Locking remains owned
/// by the existing tracker and queue implementations.
/// </remarks>
internal sealed class VulkanLifetimeAuthority(
    VulkanResourceLifetimeTracker tracker,
    VulkanResourceRetirementQueue retirement)
{
    internal VulkanResourceLifetimeTracker Tracker { get; } =
        tracker ?? throw new ArgumentNullException(nameof(tracker));
    internal VulkanResourceRetirementQueue Retirement { get; } =
        retirement ?? throw new ArgumentNullException(nameof(retirement));
    internal System.Collections.Concurrent.ConcurrentDictionary<ulong, string> LivePipelineLayoutHandles { get; } = new();
    internal VulkanImageViewLifetimeState ImageViews { get; } = new();
}
