using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe class VulkanUpscaleBridgeSharedSemaphore(
    string name,
    IOpenGlVendorUpscaleBackendCapability renderer,
    VkSemaphore vkSemaphore,
    uint glSemaphore) : IDisposable
{
    private readonly IOpenGlVendorUpscaleBackendCapability _renderer = renderer;
    private bool _disposed;

    public string Name { get; } = name;
    public VkSemaphore VulkanSemaphore { get; } = vkSemaphore;
    public uint GlSemaphore { get; } = glSemaphore;

    internal void DestroyVulkanResources(Vk api, Device device)
    {
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
