namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record VulkanPublishedDescriptorSetSnapshot(
        ulong Generation,
        VulkanResourceLifetimeKey[] References,
        VulkanPublishedDescriptorImageReference[] ImageReferences,
        uint[] ReflectedImageBindings,
        bool HasReflection);
}
