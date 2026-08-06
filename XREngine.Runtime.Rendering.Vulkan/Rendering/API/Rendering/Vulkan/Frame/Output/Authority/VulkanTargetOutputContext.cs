using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Output-owned native access required by explicit render targets. The target
/// drivers receive this focused capability instead of a renderer backlink.
/// </summary>
internal sealed unsafe class VulkanTargetOutputContext
{
    private readonly VulkanRenderer _renderer;

    internal VulkanTargetOutputContext(VulkanRenderer renderer)
        => _renderer = renderer;

    internal Vk VulkanApi => _renderer.VulkanApi;
    internal Instance Instance => _renderer.Instance;
    internal PhysicalDevice PhysicalDevice => _renderer.PhysicalDevice;
    internal Device Device => _renderer.Device;
    internal Queue GraphicsQueue => _renderer.GraphicsQueue;
    internal Queue PresentQueue => _renderer.PresentQueue;
    internal SurfaceKHR TargetSurface => _renderer.TargetSurface;
    internal VulkanDeviceContext DeviceContext => _renderer.DeviceContext;

    internal void CreateDesktopFinalOutput()
        => _renderer.CreateDesktopFinalOutput();

    internal void DestroyDesktopFinalOutput()
        => _renderer.DestroyDesktopFinalOutput();

    internal KhrSurface RequireSurfaceApi()
        => _renderer.RequireSurfaceApi();

    internal void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
        => _renderer.ThrowIfVulkanDeviceOperationNotAdmitted(operation);

    internal bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
        => _renderer.TryAdmitVulkanDeviceOperation(operation, out failureReason);

    internal void NotifyVulkanFenceCompleted(Fence fence)
        => _renderer.NotifyVulkanFenceCompleted(fence);

    internal Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner)
        => _renderer.CreateVulkanCommandPoolTracked(ref createInfo, out pool, owner);

    internal Result AllocateVulkanCommandBufferTracked(ref CommandBufferAllocateInfo allocateInfo, out CommandBuffer commandBuffer, string owner)
        => _renderer.AllocateVulkanCommandBufferTracked(ref allocateInfo, out commandBuffer, owner);

    internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
        => _renderer.ResetVulkanCommandPoolTracked(pool, owner);

    internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        => _renderer.DestroyCommandPoolHostSynchronized(pool);

    internal Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner)
        => _renderer.CreateVulkanImageTracked(ref createInfo, out image, owner);

    internal void DestroyVulkanImageImmediateTracked(Image image, string owner)
        => _renderer.DestroyVulkanImageImmediateTracked(image, owner);

    internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(Image image, MemoryPropertyFlags requiredProperties)
        => _renderer.AllocateImageMemoryWithFallback(image, requiredProperties);

    internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(Buffer buffer, MemoryPropertyFlags requiredProperties)
        => _renderer.AllocateBufferMemoryWithFallback(buffer, requiredProperties);

    internal void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
        => _renderer.FreeMemoryAllocation(allocation);

    internal void TrackLiveBuffer(Buffer buffer, string owner)
        => _renderer.TrackLiveBuffer(buffer, owner);

    internal void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
        => _renderer.TrackExternalBufferAllocation(buffer, in allocation);

    internal void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
        => _renderer.DestroyBufferRaw(buffer, memory);

    internal bool TryBeginDestroyImageView(ImageView imageView, string owner)
        => _renderer.TryBeginDestroyImageView(imageView, owner);

    internal void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
        => _renderer.TrackLiveImageView(imageView, in createInfo, owner);

    internal Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller)
        => _renderer.SubmitToQueueTracked(queue, ref submitInfo, fence, caller);

    internal bool TryMapMemoryAllocation(VulkanMemoryAllocation allocation, ulong offset, ulong length, out void* mapped)
        => _renderer.TryMapMemoryAllocation(allocation, offset, length, out mapped);

    internal void UnmapMemoryAllocation(VulkanMemoryAllocation allocation)
        => _renderer.UnmapMemoryAllocation(allocation);

    internal void MarkDeviceLost(string reason, string operation, Result result)
        => _renderer.MarkDeviceLost(reason, operation, result);
}
