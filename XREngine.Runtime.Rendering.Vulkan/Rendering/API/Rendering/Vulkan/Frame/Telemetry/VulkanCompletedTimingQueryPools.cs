using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable query-pool completion facts produced by timing readback.
/// </summary>
internal readonly record struct VulkanCompletedTimingQueryPools(
    QueryPool FrameTiming,
    QueryPool GpuProfiler);
