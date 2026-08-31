namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Defers descriptor-owner eviction until a normal frame preparation boundary.
/// </summary>
internal readonly record struct VulkanSupersededBufferDescriptorOwner(
    VulkanResourceLifetimeKey ResourceKey,
    ulong Generation);
