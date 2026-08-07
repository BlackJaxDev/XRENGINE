using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanStreamlineDeviceBinding(
    Device Device,
    Instance Instance,
    PhysicalDevice PhysicalDevice,
    uint ComputeQueueIndex,
    uint ComputeQueueFamily,
    uint GraphicsQueueIndex,
    uint GraphicsQueueFamily,
    uint OpticalFlowQueueIndex,
    uint OpticalFlowQueueFamily,
    bool UsesNativeOpticalFlow,
    bool DlssProvisioned,
    bool FrameGenerationProvisioned,
    bool FrameGenerationSwapchainIncludesDlss);