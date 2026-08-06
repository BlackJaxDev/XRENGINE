using Silk.NET.Vulkan;
using System.Threading;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe class VulkanUpscaleBridgeSharedSemaphore(
    ulong allocationGeneration,
    string name,
    IOpenGlVendorUpscaleBackendCapability renderer,
    VkSemaphore vkSemaphore,
    uint glSemaphore) : IDisposable
{
    private readonly IOpenGlVendorUpscaleBackendCapability _renderer = renderer;
    private bool _disposed;
    private int _vulkanResourcesDestroyed;

    /// <summary>Identifies the sidecar-device allocation transaction that owns this semaphore.</summary>
    public ulong AllocationGeneration { get; } = allocationGeneration;
    /// <summary>Identifies the sidecar-device publication transaction that made this semaphore visible.</summary>
    public ulong PublicationGeneration { get; private set; }
    public string Name { get; } = name;
    public VkSemaphore VulkanSemaphore { get; } = vkSemaphore;
    public uint GlSemaphore { get; } = glSemaphore;
    public ulong VulkanSemaphoreAllocationGeneration => AllocationGeneration;
    public ulong GlSemaphoreAllocationGeneration => AllocationGeneration;
    public ulong VulkanSemaphorePublicationGeneration => PublicationGeneration;
    public ulong GlSemaphorePublicationGeneration => PublicationGeneration;

    internal void Publish(ulong publicationGeneration)
    {
        if (publicationGeneration == 0 || publicationGeneration != AllocationGeneration)
            throw new InvalidOperationException("A bridge semaphore can only be published by its owning sidecar allocation generation.");
        PublicationGeneration = publicationGeneration;
    }

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (Interlocked.Exchange(ref _vulkanResourcesDestroyed, 1) != 0)
            return;

        if (VulkanSemaphore.Handle != 0)
            api.DestroySemaphore(device, VulkanSemaphore, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _renderer.DeleteSemaphore(GlSemaphore);
    }
}
