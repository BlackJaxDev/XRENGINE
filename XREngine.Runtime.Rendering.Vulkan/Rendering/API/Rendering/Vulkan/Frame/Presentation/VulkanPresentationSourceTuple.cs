using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable publication consumed from final-source validation through submit.
/// Native handles are always paired with the allocation/publication generation
/// that made them valid; logical wrappers are retained only for diagnostics and
/// readback ownership.
/// </summary>
internal readonly record struct VulkanPresentationSourceTuple(
    ulong LogicalEpoch,
    XRTexture? ColorTexture,
    XRFrameBuffer? FrameBuffer,
    FrameOpContext Context,
    ulong DescriptorResourceEpoch,
    Image Image,
    ulong ImageAllocationGeneration,
    ImageView ImageView,
    ulong ImageViewGeneration,
    Sampler Sampler,
    ulong SamplerGeneration,
    Format Format,
    ImageAspectFlags Aspect,
    SampleCountFlags Samples,
    ImageLayout ExpectedLayout,
    uint Width,
    uint Height,
    DescriptorSet DescriptorSet,
    ulong DescriptorSetGeneration,
    int DescriptorSlot,
    ulong DescriptorPublicationGeneration,
    CommandBuffer OwningCommandArtifact,
    ulong OwningCommandArtifactGeneration)
{
    internal bool HasLogicalSource =>
        ColorTexture is not null || FrameBuffer is not null;

    internal bool IsComplete =>
        !HasLogicalSource ||
        LogicalEpoch != 0 &&
        Image.Handle != 0 &&
        DescriptorResourceEpoch != 0 &&
        ImageAllocationGeneration != 0 &&
        ImageView.Handle != 0 &&
        ImageViewGeneration != 0 &&
        Sampler.Handle != 0 &&
        SamplerGeneration != 0 &&
        ExpectedLayout != ImageLayout.Undefined &&
        Width != 0 &&
        Height != 0 &&
        DescriptorSet.Handle != 0 &&
        DescriptorSetGeneration != 0 &&
        DescriptorSlot >= 0 &&
        DescriptorPublicationGeneration != 0 &&
        OwningCommandArtifact.Handle != 0 &&
        OwningCommandArtifactGeneration != 0;
}
