namespace XREngine.Rendering.Vulkan;

/// <summary>Exact per-draw heap-root state consumed by a recorded secondary.</summary>
internal readonly record struct VulkanDescriptorHeapDrawIdentity(
    DescriptorHeapPushDataIdentity PushData,
    VkMeshRenderer.MeshDrawPushConstants RootConstants)
{
    internal bool IsComplete => PushData.IsComplete;
}
