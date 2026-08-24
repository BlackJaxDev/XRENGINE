using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>Result of evaluating one physical-device candidate.</summary>
internal readonly record struct VulkanPhysicalDeviceSelectionResult(
    PhysicalDevice PhysicalDevice,
    VulkanPhysicalDeviceCapabilitySnapshot Capabilities,
    QueueFamilyIndices QueueFamilies,
    bool IsSuitable,
    bool OpenXrRequestedDeviceMatched,
    bool RequiredExtensionsSupported,
    bool SwapchainAdequate,
    bool SupportsRayTracing)
{
    public static VulkanPhysicalDeviceSelectionResult Rejected(
        in VulkanPhysicalDeviceSelectionRequest request,
        bool openXrRequestedDeviceMatched,
        bool requiredExtensionsSupported,
        bool swapchainAdequate)
        => new(
            request.PhysicalDevice,
            request.Capabilities,
            default,
            false,
            openXrRequestedDeviceMatched,
            requiredExtensionsSupported,
            swapchainAdequate,
            false);
}
