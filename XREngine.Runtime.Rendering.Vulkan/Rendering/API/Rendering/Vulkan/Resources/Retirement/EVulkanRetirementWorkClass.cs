namespace XREngine.Rendering.Vulkan;

/// <summary>Separates retirement work into independently metered native destruction classes.</summary>
internal enum EVulkanRetirementWorkClass
{
    Image,
    ImageView,
    Sampler,
    Buffer,
    Pipeline,
    PipelineLayout,
    Descriptor,
    QueryPool,
    Framebuffer,
    CommandArtifact,
    Callback,
}
