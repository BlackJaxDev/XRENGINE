using XREngine.Rendering.Vulkan;

namespace XREngine;

/// <summary>
/// Captures the effective Vulkan robustness backend selection.
/// </summary>
public readonly record struct EffectiveVulkanRobustnessSettings(
    EVulkanAllocatorBackend AllocatorBackend,
    EVulkanSynchronizationBackend SyncBackend,
    EVulkanDescriptorUpdateBackend DescriptorUpdateBackend,
    bool DynamicUniformBufferEnabled);
