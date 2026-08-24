using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Normalized OpenXR runtime device selection. Runtime querying remains at the
/// composition boundary; device admission only compares the published handle.
/// </summary>
internal readonly record struct VulkanOpenXrRequestedDeviceFacts(
    bool HasRequestedDevice,
    nint RequestedDeviceHandle)
{
    public static VulkanOpenXrRequestedDeviceFacts None => new(false, 0);

    public bool Matches(PhysicalDevice physicalDevice)
        => !HasRequestedDevice || (nint)physicalDevice.Handle == RequestedDeviceHandle;
}
