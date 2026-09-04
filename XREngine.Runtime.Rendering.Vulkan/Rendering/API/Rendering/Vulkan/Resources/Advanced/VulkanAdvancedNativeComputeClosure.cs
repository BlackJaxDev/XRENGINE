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
           ActiveTiles.NativeBuffer.Handle != 0 &&
           KernelTiles.NativeBuffer.Handle != 0 &&
           ClassificationCounters.NativeBuffer.Handle != 0 &&
           DispatchArguments.NativeBuffer.Handle != 0 &&
           KernelCounts.NativeBuffer.Handle != 0 &&
           FroxelGrid.NativeBuffer.Handle != 0 &&
           LightIndices.NativeBuffer.Handle != 0 &&
           LightingCounters.NativeBuffer.Handle != 0 &&
           IdentityDescriptor.ImageView.Handle != 0 &&
           MetadataDescriptor.ImageView.Handle != 0 &&
           DepthDescriptor.ImageView.Handle != 0 &&
           HdrDescriptor.ImageView.Handle != 0 &&
           VelocityDescriptor.ImageView.Handle != 0 &&
           ReactiveDescriptor.ImageView.Handle != 0 &&
           ShadingDiagnosticsDescriptor.ImageView.Handle != 0;
}
