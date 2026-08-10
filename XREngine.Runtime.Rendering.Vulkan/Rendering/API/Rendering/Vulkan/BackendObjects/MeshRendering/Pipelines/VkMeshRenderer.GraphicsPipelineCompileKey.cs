namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable key for an in-flight graphics pipeline compile.</summary>
internal readonly record struct VulkanGraphicsPipelineCompileKey(
    VulkanGraphicsPipelineKey Pipeline);
