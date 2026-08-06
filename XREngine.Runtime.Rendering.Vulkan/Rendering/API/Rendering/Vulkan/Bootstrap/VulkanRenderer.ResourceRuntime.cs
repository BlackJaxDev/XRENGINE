namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal VulkanResourceRuntime ResourceRuntime => _resourceRuntime;
    internal VulkanBackendObjectRegistry ResourceBackendObjects => ResourceRuntime.BackendObjects;
    internal VulkanDescriptorManager ResourceDescriptors => ResourceRuntime.Descriptors;
    internal VulkanAllocationAuthority ResourceAllocations => ResourceRuntime.Allocations;
    internal VulkanTextureUploadService ResourceUploads => ResourceRuntime.Uploads;
    internal VulkanLifetimeAuthority ResourceLifetime => ResourceRuntime.Lifetime;
}
