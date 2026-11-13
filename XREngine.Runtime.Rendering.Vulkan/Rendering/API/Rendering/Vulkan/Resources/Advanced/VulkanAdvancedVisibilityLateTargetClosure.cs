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
    /// <summary>
    /// Descriptor tables for each view: 0 = pyramid build, 1 = late compute
    /// test (pyramid in <c>SHADER_READ_ONLY_OPTIMAL</c>), 2 = late meshlet
    /// raster (pyramid in <c>GENERAL</c>, the layout the late compute pass
    /// leaves for the rest of the frame).
    /// </summary>
    internal const int DescriptorRolesPerView = 3;

    internal bool IsValid
        => DepthGroup is { IsAllocated: true } &&
           PyramidGroup is { IsAllocated: true } &&
           PyramidSampledDescriptors is { Length: > 0 } &&
           PyramidStorageDescriptors is { Length: > 0 } &&
           // Phase 5.2 deliberately has one tile-local level.  Building a
           // global mip chain in one dispatch would require a device-wide
           // barrier, which Vulkan compute workgroups cannot provide.
           DispatchCount == 1 &&
           PyramidSampledDescriptors.Length >= checked(DispatchCount * (int)ViewCount) &&
           PyramidStorageDescriptors.Length >= checked(DispatchCount * (int)ViewCount) &&
           LateSampledDescriptors is { Length: > 0 } &&
           LateStorageDescriptors is { Length: > 0 } &&
           LateSampledDescriptors.Length >= ViewCount &&
           LateStorageDescriptors.Length >= ViewCount;

    internal bool IsRecordingReady
        => IsValid && DescriptorSets is { } sets &&
           DescriptorSetCount == checked(DescriptorRolesPerView * (int)ViewCount) &&
           sets.Length >= DescriptorSetCount;

    /// <summary>Returns the immutable build (0), test (1) or late raster (2) table for a view.</summary>
    internal int DescriptorIndex(uint viewIndex, int operationIndex)
        => checked((int)viewIndex * DescriptorRolesPerView + operationIndex);
}
