using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable presentation-policy identity published with every Vulkan frame.
/// This is also the authoritative benchmark identity for desktop WSI behavior.
/// </summary>
public readonly record struct VulkanPresentationProfileSnapshot(
    EVulkanPresentationProfile RequestedProfile,
    EVulkanPresentationProfile ResolvedProfile,
    EVulkanPresentMode PresentMode,
    float TargetRefreshHz,
    TimeSpan TargetInterval,
    int MaximumFramesAhead,
    bool LimiterEnabled,
    bool FrameGenerationEnabled,
    int SwapchainImageCount,
    int FrameSlotCount,
    bool ValidationEnabled,
    EVulkanRenderTargetMode RenderTargetMode,
    bool PresentIdAvailable,
    bool PresentIdEnabled,
    bool PresentWaitAvailable,
    bool PresentWaitEnabled,
    bool DisplayTimingAvailable,
    bool DisplayTimingEnabled)
{
    /// <summary>Whether this snapshot describes a created desktop swapchain.</summary>
    public bool IsValid
        => PresentMode != EVulkanPresentMode.Unknown &&
           SwapchainImageCount > 0 &&
           FrameSlotCount > 0;
}
