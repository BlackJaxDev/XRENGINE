using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>All facts required to evaluate one physical-device candidate.</summary>
internal readonly record struct VulkanPhysicalDeviceSelectionRequest(
    PhysicalDevice PhysicalDevice,
    VulkanPhysicalDeviceCapabilitySnapshot Capabilities,
    VulkanOutputDeviceRequirements OutputRequirements,
    VulkanOutputDeviceProbeFacts OutputProbe,
    VulkanDeviceExtensionRequirements ExtensionRequirements,
    VulkanOpenXrRequestedDeviceFacts OpenXrRequestedDevice);
