namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow command-authority port used to close deferred recording-publication
/// windows before a native resource generation enters retirement.
/// </summary>
internal sealed class VulkanRetirementDependencyPublicationPort(
    VulkanCommandRuntime commandRuntime)
{
    private readonly VulkanCommandRuntime _commandRuntime = commandRuntime;

    internal void Publish(VulkanResourceLifetimeKey resourceKey)
        => _commandRuntime.PublishTrackingDependenciesBeforeResourceRetirement(
            resourceKey);
}
