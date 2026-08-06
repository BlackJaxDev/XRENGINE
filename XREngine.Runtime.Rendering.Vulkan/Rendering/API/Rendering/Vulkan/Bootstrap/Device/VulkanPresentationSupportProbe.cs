using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Output-owned presentation query used during physical-device selection.
/// The probe is invoked only by device creation and never retained by output
/// runtime objects.
/// </summary>
internal delegate Result VulkanPresentationSupportProbe(
    PhysicalDevice physicalDevice,
    uint queueFamilyIndex,
    out bool supportsPresentation);
