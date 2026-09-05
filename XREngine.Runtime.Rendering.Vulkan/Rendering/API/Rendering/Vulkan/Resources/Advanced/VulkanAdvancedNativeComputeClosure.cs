using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable native-resource closure for one advanced compute operation.
/// Every group and buffer originates from the graph generation accepted by
/// the primary frame plan. Recording must use this closure rather than a
/// later logical-resource lookup, which would allow an ABA replacement after
/// command admission.
/// </summary>
internal readonly record struct VulkanAdvancedNativeComputeClosure(
    ulong GraphRevision,
    VulkanPhysicalImageGroup Identity,
    VulkanPhysicalImageGroup Metadata,
    VulkanPhysicalImageGroup Depth,
    VulkanPhysicalImageGroup Hdr,
    VulkanPhysicalImageGroup Velocity,
    VulkanPhysicalImageGroup Reactive,
    VulkanPhysicalImageGroup ShadingDiagnostics,
    VulkanPhysicalImageGroup AmbientOcclusion,
    VulkanFrozenBufferBarrier ActiveTiles,
    VulkanFrozenBufferBarrier KernelTiles,
    VulkanFrozenBufferBarrier ClassificationCounters,
    VulkanFrozenBufferBarrier DispatchArguments,
    VulkanFrozenBufferBarrier KernelCounts,
    VulkanFrozenBufferBarrier FroxelGrid,
    VulkanFrozenBufferBarrier LightIndices,
    VulkanFrozenBufferBarrier LightingCounters,
    DescriptorImageInfo IdentityDescriptor,
    DescriptorImageInfo MetadataDescriptor,
    DescriptorImageInfo DepthDescriptor,
    DescriptorImageInfo HdrDescriptor,
    DescriptorImageInfo VelocityDescriptor,
    DescriptorImageInfo ReactiveDescriptor,
    DescriptorImageInfo ShadingDiagnosticsDescriptor,
    DescriptorImageInfo AmbientOcclusionStorageDescriptor,
    DescriptorImageInfo AmbientOcclusionSampledDescriptor,
    uint ViewIndex)
{
    internal bool IsValid
        => GraphRevision != 0u &&
           Identity is { IsAllocated: true } &&
           Metadata is { IsAllocated: true } &&
           Depth is { IsAllocated: true } &&
           Hdr is { IsAllocated: true } &&
           Velocity is { IsAllocated: true } &&
           Reactive is { IsAllocated: true } &&
           ShadingDiagnostics is { IsAllocated: true } &&
           AmbientOcclusion is { IsAllocated: true } &&
           HasFrozenRange(ActiveTiles) &&
           HasFrozenRange(KernelTiles) &&
           HasFrozenRange(ClassificationCounters) &&
           HasFrozenRange(DispatchArguments) &&
           HasFrozenRange(KernelCounts) &&
           HasFrozenRange(FroxelGrid) &&
           HasFrozenRange(LightIndices) &&
           HasFrozenRange(LightingCounters) &&
           IdentityDescriptor.ImageView.Handle != 0 &&
           MetadataDescriptor.ImageView.Handle != 0 &&
           DepthDescriptor.ImageView.Handle != 0 &&
           HdrDescriptor.ImageView.Handle != 0 &&
           VelocityDescriptor.ImageView.Handle != 0 &&
           ReactiveDescriptor.ImageView.Handle != 0 &&
           ShadingDiagnosticsDescriptor.ImageView.Handle != 0 &&
           AmbientOcclusionStorageDescriptor.ImageView.Handle != 0 &&
           AmbientOcclusionSampledDescriptor.ImageView.Handle != 0 &&
           AmbientOcclusionSampledDescriptor.Sampler.Handle != 0;

    private static bool HasFrozenRange(in VulkanFrozenBufferBarrier barrier)
        => barrier.NativeBuffer.Handle != 0 &&
           barrier.NativeGeneration != 0u &&
           barrier.NativeOffset == 0u &&
           barrier.NativeSize != 0u;
}
