using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable output facts selected before Vulkan instance and device creation.</summary>
internal sealed record VulkanStreamlineProvisioningSnapshot(
    bool DlssProvisioned,
    bool FrameGenerationProvisioned,
    string[] RequiredInstanceExtensions,
    string[] RequiredDeviceExtensions,
    string[] RequiredFeatures12,
    string[] RequiredFeatures13,
    NvidiaDlssManager.Native.StreamlineQueueRequirements QueueRequirements,
    uint MinimumApiVersion,
    uint GraphicsQueueFamily = 0,
    uint GraphicsQueueIndex = 0,
    uint ComputeQueueFamily = 0,
    uint ComputeQueueIndex = 0,
    uint OpticalFlowQueueFamily = 0,
    uint OpticalFlowQueueIndex = 0)
{
    internal static VulkanStreamlineProvisioningSnapshot Empty { get; } = new(
        false,
        false,
        [],
        [],
        [],
        [],
        default,
        Vk.Version11);

    internal bool HasRequirements
        => RequiredInstanceExtensions.Length > 0
            || RequiredDeviceExtensions.Length > 0
            || RequiredFeatures12.Length > 0
            || RequiredFeatures13.Length > 0
            || QueueRequirements.GraphicsQueues > 0
            || QueueRequirements.ComputeQueues > 0
            || QueueRequirements.OpticalFlowQueues > 0;
}
