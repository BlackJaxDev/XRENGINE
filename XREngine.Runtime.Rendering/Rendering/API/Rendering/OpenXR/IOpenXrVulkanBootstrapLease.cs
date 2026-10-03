namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Renderer-owned OpenXR instance retained through Vulkan device and session lifetime.</summary>
public interface IOpenXrVulkanBootstrapLease : IDisposable
{
    ulong InstanceHandle { get; }
    IReadOnlyList<string> InstanceExtensions { get; }
    IOpenXrNativeGraphicsBorrow BorrowNativeGraphicsDispatch();
    bool TryCreateVulkanInstance(
        nint createInfo, nint getInstanceProcAddr,
        out nint instanceHandle, out uint vulkanResult, out string? failureReason);
    bool TryGetRequestedVulkanPhysicalDevice(nint instanceHandle, out nint physicalDeviceHandle, out string? failureReason);
    bool TryCreateVulkanDevice(
        nint physicalDeviceHandle, nint createInfo, nint getInstanceProcAddr,
        out nint deviceHandle, out uint vulkanResult, out string? failureReason);
    bool Invalidate(string reason);
    void AbandonOnDispose(string reason);
    int TryDestroyAfterDeviceLoss(string reason);
}
