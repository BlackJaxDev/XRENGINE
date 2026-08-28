namespace XREngine.Rendering.Vulkan;

/// <summary>Independent generations captured by sealed native artifacts.</summary>
internal readonly record struct VulkanNativeDependencyGeneration(
    ulong Topology,
    ulong Content);
