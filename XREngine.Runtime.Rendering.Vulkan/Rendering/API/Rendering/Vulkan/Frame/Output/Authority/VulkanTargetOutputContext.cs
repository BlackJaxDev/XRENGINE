using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Output-owned native access required by explicit render targets. The target
/// drivers receive this focused capability instead of a renderer backlink.
/// </summary>
internal sealed class VulkanTargetOutputContext
{
    private readonly IVulkanTargetOutputHost _host;

    internal VulkanTargetOutputContext(IVulkanTargetOutputHost host)
        => _host = host;

    internal Vk VulkanApi => _host.VulkanApi;
    internal Instance Instance => _host.Instance;
    internal PhysicalDevice PhysicalDevice => _host.PhysicalDevice;
    internal Device Device => _host.Device;
    internal Queue GraphicsQueue => _host.GraphicsQueue;
    internal Queue PresentQueue => _host.PresentQueue;
    internal SurfaceKHR TargetSurface => _host.TargetSurface;
    internal uint GraphicsQueueFamilyIndex => _host.GraphicsQueueFamilyIndex;
    internal uint PresentQueueFamilyIndex => _host.PresentQueueFamilyIndex;

    internal KhrSurface RequireSurfaceApi()
        => _host.RequireSurfaceApi();

    internal void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        _host.ThrowIfVulkanDeviceOperationNotAdmitted(operation);
    }

    internal bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
        => _host.TryAdmitVulkanDeviceOperation(operation, out failureReason);

    internal void NotifyVulkanFenceCompleted(Fence fence)
        => _host.NotifyVulkanFenceCompleted(fence);

    internal Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner)
        => _host.CreateVulkanCommandPoolTracked(ref createInfo, out pool, owner);

    internal Result AllocateVulkanCommandBufferTracked(ref CommandBufferAllocateInfo allocateInfo, out CommandBuffer commandBuffer, string owner)
        => _host.AllocateVulkanCommandBufferTracked(ref allocateInfo, out commandBuffer, owner);

    internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
        => _host.ResetVulkanCommandPoolTracked(pool, owner);

    internal Result BeginCommandBufferTracked(
        CommandBuffer commandBuffer,
        ref CommandBufferBeginInfo beginInfo,
        string owner)
        => _host.BeginCommandBufferTracked(commandBuffer, ref beginInfo, owner);

    internal Result EndCommandBufferTracked(CommandBuffer commandBuffer)
        => _host.EndCommandBufferTracked(commandBuffer);

    internal void TrackCommandBufferResource(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        string owner)
        => _host.TrackCommandBufferResource(commandBuffer, type, handle, owner);

    internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
        => _host.DestroyCommandPoolHostSynchronized(pool);

    internal Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner)
        => _host.CreateVulkanImageTracked(ref createInfo, out image, owner);

    internal void DestroyVulkanImageImmediateTracked(Image image, string owner)
        => _host.DestroyVulkanImageImmediateTracked(image, owner);

    internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(Image image, MemoryPropertyFlags requiredProperties)
        => _host.AllocateImageMemoryWithFallback(image, requiredProperties);

    internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(Buffer buffer, MemoryPropertyFlags requiredProperties)
        => _host.AllocateBufferMemoryWithFallback(buffer, requiredProperties);

    internal void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
        => _host.FreeMemoryAllocation(allocation);

    internal void TrackLiveBuffer(Buffer buffer, string owner)
        => _host.TrackLiveBuffer(buffer, owner);

    internal void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
        => _host.TrackExternalBufferAllocation(buffer, in allocation);

    internal void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
        => _host.DestroyBufferRaw(buffer, memory);

    internal bool TryBeginDestroyImageView(ImageView imageView, string owner)
        => _host.TryBeginDestroyImageView(imageView, owner);

    internal void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
        => _host.TrackLiveImageView(imageView, in createInfo, owner);

    internal Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller)
        => _host.SubmitToQueueTracked(queue, ref submitInfo, fence, caller);

    internal Result PresentToQueueTracked(KhrSwapchain swapchainApi, Queue queue, ref PresentInfoKHR presentInfo, string caller)
        => _host.PresentToQueueTracked(swapchainApi, queue, ref presentInfo, caller);

    internal bool TryReadMappedMemory<TState>(VulkanMemoryAllocation allocation, ulong offset, ulong length, TState state, VulkanMappedMemoryReadCallback<TState> callback)
        => _host.TryReadMappedMemory(allocation, offset, length, state, callback);

    internal bool TryWriteMappedMemory<TState>(VulkanMemoryAllocation allocation, ulong offset, ulong length, TState state, VulkanMappedMemoryWriteCallback<TState> callback)
        => _host.TryWriteMappedMemory(allocation, offset, length, state, callback);

    internal void MarkDeviceLost(string reason, string operation, Result result)
        => _host.MarkDeviceLost(reason, operation, result);

    internal void ObserveNativeResult(string operation, Result result)
        => _host.ObserveNativeResult(operation, result);
}
