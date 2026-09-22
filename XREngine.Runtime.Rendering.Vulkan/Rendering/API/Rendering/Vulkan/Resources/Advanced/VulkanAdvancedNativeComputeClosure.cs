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
    VulkanPhysicalImageGroup? IdentityMultisample,
    VulkanPhysicalImageGroup? MetadataMultisample,
    VulkanPhysicalImageGroup? DepthMultisample,
    VulkanPhysicalImageGroup? SelectionMultisample,
    VulkanPhysicalImageGroup? SamplePositionMultisample,
    VulkanAdvancedNativeImageClosure IdentityResource,
    VulkanAdvancedNativeImageClosure MetadataResource,
    VulkanAdvancedNativeImageClosure DepthResource,
    VulkanAdvancedNativeImageClosure HdrResource,
    VulkanAdvancedNativeImageClosure VelocityResource,
    VulkanAdvancedNativeImageClosure ReactiveResource,
    VulkanAdvancedNativeImageClosure ShadingDiagnosticsResource,
    VulkanAdvancedNativeImageClosure AmbientOcclusionResource,
    VulkanAdvancedNativeImageClosure IdentityMultisampleResource,
    VulkanAdvancedNativeImageClosure MetadataMultisampleResource,
    VulkanAdvancedNativeImageClosure DepthMultisampleResource,
    VulkanAdvancedNativeImageClosure SelectionMultisampleResource,
    VulkanAdvancedNativeImageClosure SamplePositionMultisampleResource,
    Sampler Sampler,
    ulong SamplerGeneration,
    VulkanFrozenBufferBarrier ActiveTiles,
    VulkanFrozenBufferBarrier KernelTiles,
    VulkanFrozenBufferBarrier ClassificationCounters,
    VulkanFrozenBufferBarrier DispatchArguments,
    VulkanFrozenBufferBarrier KernelCounts,
    VulkanFrozenBufferBarrier FroxelGrid,
    VulkanFrozenBufferBarrier LightIndices,
    VulkanFrozenBufferBarrier LightingCounters,
    VulkanFrozenBufferBarrier FroxelDecalGrid,
    VulkanFrozenBufferBarrier DecalIndices,
    DescriptorImageInfo IdentityDescriptor,
    DescriptorImageInfo MetadataDescriptor,
    DescriptorImageInfo DepthDescriptor,
    DescriptorImageInfo HdrDescriptor,
    DescriptorImageInfo VelocityDescriptor,
    DescriptorImageInfo ReactiveDescriptor,
    DescriptorImageInfo ShadingDiagnosticsDescriptor,
    DescriptorImageInfo AmbientOcclusionStorageDescriptor,
    DescriptorImageInfo AmbientOcclusionSampledDescriptor,
    DescriptorImageInfo IdentityMultisampleDescriptor,
    DescriptorImageInfo MetadataMultisampleDescriptor,
    DescriptorImageInfo DepthMultisampleDescriptor,
    DescriptorImageInfo SelectionMultisampleDescriptor,
    DescriptorImageInfo SamplePositionMultisampleDescriptor,
    uint MsaaSampleCount,
    uint ViewIndex)
{
    internal VulkanAdvancedNativeShadingRootBinding ShadingAddressRoot { get; init; }
    internal VulkanAdvancedDdgiSurfaceClosure DdgiSurface { get; init; }

    internal bool IsValid
        => GraphRevision != 0u && DdgiSurface.IsValid &&
           Identity is { IsAllocated: true } &&
           Metadata is { IsAllocated: true } &&
           Depth is { IsAllocated: true } &&
           Hdr is { IsAllocated: true } &&
           Velocity is { IsAllocated: true } &&
           Reactive is { IsAllocated: true } &&
           ShadingDiagnostics is { IsAllocated: true } &&
           AmbientOcclusion is { IsAllocated: true } &&
           IdentityResource.IsValid && MetadataResource.IsValid &&
           DepthResource.IsValid && HdrResource.IsValid &&
           VelocityResource.IsValid && ReactiveResource.IsValid &&
           ShadingDiagnosticsResource.IsValid && AmbientOcclusionResource.IsValid &&
           Sampler.Handle != 0 && SamplerGeneration != 0 &&
           HasFrozenRange(ActiveTiles) &&
           HasFrozenRange(KernelTiles) &&
           HasFrozenRange(ClassificationCounters) &&
           HasFrozenRange(DispatchArguments) &&
           HasFrozenRange(KernelCounts) &&
           HasFrozenRange(FroxelGrid) &&
           HasFrozenRange(LightIndices) &&
           HasFrozenRange(LightingCounters) &&
           HasFrozenRange(FroxelDecalGrid) &&
           HasFrozenRange(DecalIndices) &&
           IdentityDescriptor.ImageView.Handle != 0 &&
           MetadataDescriptor.ImageView.Handle != 0 &&
           DepthDescriptor.ImageView.Handle != 0 &&
           HdrDescriptor.ImageView.Handle != 0 &&
           VelocityDescriptor.ImageView.Handle != 0 &&
           ReactiveDescriptor.ImageView.Handle != 0 &&
           ShadingDiagnosticsDescriptor.ImageView.Handle != 0 &&
           AmbientOcclusionStorageDescriptor.ImageView.Handle != 0 &&
           AmbientOcclusionSampledDescriptor.ImageView.Handle != 0 &&
           AmbientOcclusionSampledDescriptor.Sampler.Handle != 0 &&
           (MsaaSampleCount <= 1u ||
            IdentityMultisample is { IsAllocated: true } &&
            MetadataMultisample is { IsAllocated: true } &&
            DepthMultisample is { IsAllocated: true } &&
            SelectionMultisample is { IsAllocated: true } &&
            SamplePositionMultisample is { IsAllocated: true } &&
            IdentityMultisampleResource.IsValid &&
            MetadataMultisampleResource.IsValid &&
            DepthMultisampleResource.IsValid &&
            SelectionMultisampleResource.IsValid &&
            SamplePositionMultisampleResource.IsValid &&
            IdentityMultisampleDescriptor.ImageView.Handle != 0 &&
            MetadataMultisampleDescriptor.ImageView.Handle != 0 &&
            DepthMultisampleDescriptor.ImageView.Handle != 0 &&
            SelectionMultisampleDescriptor.ImageView.Handle != 0 &&
            SamplePositionMultisampleDescriptor.ImageView.Handle != 0 &&
            IdentityMultisampleDescriptor.Sampler.Handle != 0 &&
            MetadataMultisampleDescriptor.Sampler.Handle != 0 &&
            DepthMultisampleDescriptor.Sampler.Handle != 0 &&
            SelectionMultisampleDescriptor.Sampler.Handle != 0);

    internal bool UsesMultisampleVisibility => MsaaSampleCount > 1u;

    private static bool HasFrozenRange(in VulkanFrozenBufferBarrier barrier)
        => barrier.NativeBuffer.Handle != 0 &&
           barrier.NativeGeneration != 0u &&
           barrier.NativeOffset == 0u &&
           barrier.NativeSize != 0u;
}
