namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable indexed draw arguments for one CPU-direct visibility payload.
/// FirstInstance is always the canonical payload index so direct and indirect
/// lanes execute the same shader identity path.
/// </summary>
internal readonly record struct VulkanPreparedVisibilityDirectDraw(
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance)
{
    internal bool IsValid => IndexCount != 0u && InstanceCount != 0u;
}
