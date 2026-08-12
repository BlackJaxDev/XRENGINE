using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Behavior-only native output port implemented by the frame-loop authority.</summary>
internal interface IVulkanTargetOutputHost
{
    Vk VulkanApi { get; }
    Instance Instance { get; }
    PhysicalDevice PhysicalDevice { get; }
    Device Device { get; }
    Queue GraphicsQueue { get; }
    Queue PresentQueue { get; }
    SurfaceKHR TargetSurface { get; }
    uint GraphicsQueueFamilyIndex { get; }
    uint PresentQueueFamilyIndex { get; }
    bool StreamlineDlssProvisioned { get; }
    bool StreamlineFrameGenerationProvisioned { get; }
    VulkanStreamlineDeviceBinding CaptureStreamlineDeviceBinding();
    CommandBuffer[] CreateDesktopOutputArtifacts(int imageCount);
    int ReserveOpenXrFrameDataSlots(int desktopImageCount);
    void PublishDesktopImageTimelineValues(ulong[]? timelineValues);
    void PublishDesktopSwapchainExtent(Extent2D extent);
    void RetireDesktopOutputArtifacts(CommandBuffer[]? commandBuffers);
    void DrainRetiredDesktopCommandBuffers(int frameSlot);
    KhrSurface RequireSurfaceApi();
    void ThrowIfVulkanDeviceOperationNotAdmitted(string operation);
    bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason);
    void NotifyVulkanFenceCompleted(Fence fence);
    Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner);
    Result AllocateVulkanCommandBufferTracked(ref CommandBufferAllocateInfo allocateInfo, out CommandBuffer commandBuffer, string owner);
    Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner);
    Result EndCommandBufferTracked(CommandBuffer commandBuffer);
    void DestroyCommandPoolHostSynchronized(CommandPool pool);
    Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner);
    void DestroyVulkanImageImmediateTracked(Image image, string owner);
    VulkanMemoryAllocation AllocateImageMemoryWithFallback(Image image, MemoryPropertyFlags requiredProperties);
    VulkanMemoryAllocation AllocateBufferMemoryWithFallback(Buffer buffer, MemoryPropertyFlags requiredProperties);
    void FreeMemoryAllocation(VulkanMemoryAllocation allocation);
    void TrackLiveBuffer(Buffer buffer, string owner);
    void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation);
    void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory);
    bool TryBeginDestroyImageView(ImageView imageView, string owner);
    void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner);
    Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller);
    bool TryReadMappedMemory<TState>(VulkanMemoryAllocation allocation, ulong offset, ulong length, TState state, VulkanMappedMemoryReadCallback<TState> callback);
    bool TryWriteMappedMemory<TState>(VulkanMemoryAllocation allocation, ulong offset, ulong length, TState state, VulkanMappedMemoryWriteCallback<TState> callback);
    void MarkDeviceLost(string reason, string operation, Result result);
    void ObserveNativeResult(string operation, Result result);
}
