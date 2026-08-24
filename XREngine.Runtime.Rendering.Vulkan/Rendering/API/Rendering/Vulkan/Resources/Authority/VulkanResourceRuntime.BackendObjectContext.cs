namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    /// <summary>
    /// Publishes the backend-object identity graph constructed by the facade.
    /// Resource runtime owns the published resource-local reference, but never
    /// selects planner, command, or telemetry authorities for wrapper behavior.
    /// </summary>
    internal VulkanBackendObjectContext PublishBackendObjectContext(
        VulkanBackendObjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        VulkanBackendObjectContext? existing = BackendObjectContext;
        if (existing is not null)
            return existing;

        BackendObjectContext = context;
        DescriptorLifetime.PublishBackendObjectContext(context);
        Descriptors.PublishBackendObjectContext(context);
        FallbackTexture.PublishBackendObjectContext(context);
        BlackFallbackTexture.PublishBackendObjectContext(context);
        return context;
    }
}
