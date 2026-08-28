using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable physical-image and descriptor-view closure for one late
/// visibility operation.  The closure deliberately stores the exact image
/// groups selected by the frozen render-graph generation, not a logical name
/// that could resolve differently when commands are recorded.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityLateTargetClosure(
    VulkanPhysicalImageGroup DepthGroup,
    VulkanPhysicalImageGroup PyramidGroup,
    DescriptorImageInfo[] PyramidSampledDescriptors,
    DescriptorImageInfo[] PyramidStorageDescriptors,
    DescriptorImageInfo[] LateSampledDescriptors,
    DescriptorImageInfo[] LateStorageDescriptors,
    int DispatchCount,
    DescriptorSet[]? DescriptorSets = null,
    int DescriptorSetCount = 0,
    uint ViewCount = 1u)
{
    internal bool IsValid
        => DepthGroup is { IsAllocated: true } &&
           PyramidGroup is { IsAllocated: true } &&
           PyramidSampledDescriptors is { Length: > 0 } &&
           PyramidStorageDescriptors is { Length: > 0 } &&
           DispatchCount > 0 &&
           PyramidSampledDescriptors.Length >= checked(DispatchCount * (int)ViewCount) &&
           PyramidStorageDescriptors.Length >= checked(DispatchCount * (int)ViewCount) &&
           LateSampledDescriptors is { Length: > 0 } &&
           LateStorageDescriptors is { Length: > 0 } &&
           LateSampledDescriptors.Length >= ViewCount &&
           LateStorageDescriptors.Length >= ViewCount;

    internal bool IsRecordingReady
        => IsValid && DescriptorSets is { } sets &&
           DescriptorSetCount == checked((DispatchCount + 1) * (int)ViewCount) &&
           sets.Length >= DescriptorSetCount;

    internal int DescriptorIndex(uint viewIndex, int mipIndex)
        => checked((int)viewIndex * (DispatchCount + 1) + mipIndex);
}
