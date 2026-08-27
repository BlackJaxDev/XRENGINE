using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine;

/// <summary>
/// Captures the effective Vulkan runtime configuration.
/// </summary>
public readonly record struct EffectiveVulkanRenderSettings(
    EVulkanGpuDrivenProfile GpuDrivenProfile,
    EVulkanQueueOverlapMode QueueOverlapMode,
    EVulkanPresentationProfile PresentationProfile,
    float PresentationTargetRefreshHz,
    bool EnableDescriptorIndexing,
    bool EnableBindlessMaterialTable,
    EVulkanBindlessMaterialMode BindlessMaterialMode,
    bool ValidateDescriptorContracts,
    EVulkanGeometryFetchMode GeometryFetchMode,
    EVulkanRenderTargetMode RenderTargetMode,
    RenderBackendFallbackPolicy BackendFallbackPolicy,
    EffectiveVulkanRobustnessSettings Robustness);
