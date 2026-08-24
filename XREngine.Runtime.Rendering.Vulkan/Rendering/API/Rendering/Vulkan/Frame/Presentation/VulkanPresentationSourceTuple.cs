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
/// <param name="LogicalEpoch">The logical epoch of the frame, used to track the sequence of frames.</param>
/// <param name="ColorTexture">The color texture associated with the frame, if any.</param>
/// <param name="FrameBuffer">The framebuffer associated with the frame, if any.</param>
/// <param name="Context">The context of the frame operation, providing information about the rendering state.</param>
/// <param name="DescriptorResourceEpoch">The epoch of the descriptor resource, used for tracking changes in descriptor sets.</param>
/// <param name="Image">The Vulkan image handle associated with the frame.</param>
/// <param name="ImageAllocationGeneration">The generation of the image allocation, used to track changes in image resources.</param>
/// <param name="ImageView">The Vulkan image view handle associated with the frame.</param>
/// <param name="ImageViewGeneration">The generation of the image view, used to track changes in image view resources.</param>
/// <param name="Sampler">The Vulkan sampler handle associated with the frame.</param>
/// <param name="SamplerGeneration">The generation of the sampler, used to track changes in sampler resources.</param>
/// <param name="Format">The format of the image, indicating how pixel data is stored.</param>
/// <param name="Aspect">The aspect flags of the image, indicating which aspects of the image are being used.</param>
/// <param name="Samples">The sample count flags of the image, indicating the number of samples used for multisampling.</param>
/// <param name="ExpectedLayout">The expected layout of the image, indicating how the image is intended to be used in the rendering pipeline.</param>
/// <param name="Width">The width of the image in pixels.</param>
/// <param name="Height">The height of the image in pixels.</param>
/// <param name="DescriptorSet">The Vulkan descriptor set handle associated with the frame.</param>
/// <param name="DescriptorSetGeneration">The generation of the descriptor set, used to track changes in descriptor set resources.</param>
/// <param name="DescriptorSlot">The slot index of the descriptor set, indicating where the descriptor set is bound in the pipeline.</param>
/// <param name="DescriptorPublicationGeneration">The generation of the descriptor publication, used to track changes in descriptor publication resources.</param>
/// <param name="OwningCommandArtifact">The Vulkan command buffer handle that owns the frame, used for submitting commands to the GPU.</param>
/// <param name="OwningCommandArtifactGeneration">The generation of the owning command artifact, used to track changes in command buffer resources.</param>
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
    /// <summary>
    /// Indicates whether the presentation source has a logical source, 
    /// which is true if either the color texture or framebuffer is not null.
    /// </summary>
    internal bool HasLogicalSource =>
        ColorTexture is not null || FrameBuffer is not null;

    /// <summary>
    /// Indicates whether the presentation source is complete and valid for use,
    /// which requires that all necessary resources and handles are properly initialized and valid.
    /// </summary>
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
