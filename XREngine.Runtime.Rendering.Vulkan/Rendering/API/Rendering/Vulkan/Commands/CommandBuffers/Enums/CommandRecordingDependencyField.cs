namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the first immutable dependency that prevents command reuse.
/// </summary>
internal enum CommandRecordingDependencyField
{
    None,
    OutputPassAttachment,
    RenderArea,
    ViewMask,
    QueueFamily,
    DynamicRenderingInheritance,
    PipelineGeneration,
    PipelineLayoutGeneration,
    MeshBindingIdentity,
    IndexBufferBindingIdentity,
    VertexBufferBindingIdentity,
    BufferAllocationGeneration,
    ImageAllocationGeneration,
    ImageViewGeneration,
    SamplerAllocationGeneration,
    DescriptorLayoutGeneration,
    DescriptorSetGeneration,
    ResourcePlanGeneration,
    ExternalTargetVariant,
    FrameSlotVariant,
    DescriptorPublicationGeneration,
    DataPublicationGeneration,
    VolatileSuffixGeneration,
}
