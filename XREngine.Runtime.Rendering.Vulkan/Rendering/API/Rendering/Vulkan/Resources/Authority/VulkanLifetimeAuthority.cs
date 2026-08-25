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
    private VulkanRetirementDependencyPublicationPort? _retirementDependencyPublications;

    internal VulkanResourceLifetimeTracker Tracker { get; } =
        tracker ?? throw new ArgumentNullException(nameof(tracker));
    internal VulkanResourceRetirementQueue Retirement { get; } =
        retirement ?? throw new ArgumentNullException(nameof(retirement));
    internal System.Collections.Concurrent.ConcurrentDictionary<ulong, string> LivePipelineLayoutHandles { get; } = new();
    internal VulkanImageViewLifetimeState ImageViews { get; } = new();

    internal void ConfigureRetirementDependencyPublications(
        VulkanRetirementDependencyPublicationPort publications)
    {
        ArgumentNullException.ThrowIfNull(publications);
        VulkanRetirementDependencyPublicationPort? current = Interlocked.CompareExchange(
            ref _retirementDependencyPublications,
            publications,
            null);
        if (current is not null && !ReferenceEquals(current, publications))
        {
            throw new InvalidOperationException(
                "The Vulkan lifetime authority already owns a different retirement dependency publication port.");
        }
    }

    internal void PublishTrackingDependenciesBeforeRetirement(
        VulkanResourceLifetimeKey resourceKey)
        => Volatile.Read(ref _retirementDependencyPublications)?.Publish(resourceKey);
}
