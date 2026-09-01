namespace XREngine.Rendering.Vulkan;

/// <summary>Native ownership of one preallocated presentation fence.</summary>
internal enum EVulkanWsiPresentState : byte
{
    Free,
    Reserved,
    Enqueued,
    Quarantined,
}
