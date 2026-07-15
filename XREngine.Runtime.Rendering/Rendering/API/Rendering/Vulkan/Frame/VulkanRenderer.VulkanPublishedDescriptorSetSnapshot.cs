namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed record VulkanPublishedDescriptorSetSnapshot(
        ulong Generation,
        VulkanResourceLifetimeKey[] References,
        VulkanPublishedDescriptorImageReference[] ImageReferences,
        uint[] ReflectedImageBindings,
        bool HasReflection);
}
