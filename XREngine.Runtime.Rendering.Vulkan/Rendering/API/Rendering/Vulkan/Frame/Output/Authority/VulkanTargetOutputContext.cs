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
    private readonly VulkanTargetOutputServices _services;

    internal VulkanTargetOutputContext(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry,
        VulkanOutputRuntime outputRuntime)
        => _services = new VulkanTargetOutputServices(
            api,
            deviceContext,
            commandRuntime,
            resourceRuntime,
            telemetry,
            outputRuntime);

    internal Vk VulkanApi => _services.VulkanApi;
    internal Instance Instance => _services.Instance;
    internal PhysicalDevice PhysicalDevice => _services.PhysicalDevice;
    internal Device Device => _services.Device;
    internal Queue GraphicsQueue => _services.GraphicsQueue;
    internal Queue PresentQueue => _services.PresentQueue;
    internal SurfaceKHR TargetSurface => _services.TargetSurface;
    internal VulkanDeviceContext DeviceContext => _services.DeviceContext;
    internal VulkanResourceRuntime ResourceRuntime => _services.ResourceRuntime;

    internal KhrSurface RequireSurfaceApi()
        => _services.RequireSurfaceApi();

    internal void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        _services.ThrowIfVulkanDeviceOperationNotAdmitted(operation);
    }

    internal bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
        => _services.TryAdmitVulkanDeviceOperation(operation, out failureReason);

    internal void NotifyVulkanFenceCompleted(Fence fence)
        => _services.NotifyVulkanFenceCompleted(fence);

    internal Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner)
        => _services.CreateVulkanCommandPoolTracked(ref createInfo, out pool, owner);

    internal Result AllocateVulkanCommandBufferTracked(ref CommandBufferAllocateInfo allocateInfo, out CommandBuffer commandBuffer, string owner)
        => _services.AllocateVulkanCommandBufferTracked(ref allocateInfo, out commandBuffer, owner);

    internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
        => _services.ResetVulkanCommandPoolTracked(pool, owner);

    internal Result EndCommandBufferTracked(CommandBuffer commandBuffer)
        => _services.EndCommandBufferTracked(commandBuffer);

    internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        => _services.DestroyCommandPoolHostSynchronized(pool);

    internal Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner)
        => _services.CreateVulkanImageTracked(ref createInfo, out image, owner);

    internal void DestroyVulkanImageImmediateTracked(Image image, string owner)
        => _services.DestroyVulkanImageImmediateTracked(image, owner);

    internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(Image image, MemoryPropertyFlags requiredProperties)
        => _services.AllocateImageMemoryWithFallback(image, requiredProperties);

    internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(Buffer buffer, MemoryPropertyFlags requiredProperties)
        => _services.AllocateBufferMemoryWithFallback(buffer, requiredProperties);

    internal void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
        => _services.FreeMemoryAllocation(allocation);

    internal void TrackLiveBuffer(Buffer buffer, string owner)
        => _services.TrackLiveBuffer(buffer, owner);

    internal void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
        => _services.TrackExternalBufferAllocation(buffer, in allocation);

    internal void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
        => _services.DestroyBufferRaw(buffer, memory);

    internal bool TryBeginDestroyImageView(ImageView imageView, string owner)
        => _services.TryBeginDestroyImageView(imageView, owner);

    internal void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
        => _services.TrackLiveImageView(imageView, in createInfo, owner);

    internal Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller)
        => _services.SubmitToQueueTracked(queue, ref submitInfo, fence, caller);

    internal bool TryMapMemoryAllocation(VulkanMemoryAllocation allocation, ulong offset, ulong length, out void* mapped)
        => _services.TryMapMemoryAllocation(allocation, offset, length, out mapped);

    internal void UnmapMemoryAllocation(VulkanMemoryAllocation allocation)
        => _services.UnmapMemoryAllocation(allocation);

    internal void MarkDeviceLost(string reason, string operation, Result result)
        => _services.MarkDeviceLost(reason, operation, result);
}
