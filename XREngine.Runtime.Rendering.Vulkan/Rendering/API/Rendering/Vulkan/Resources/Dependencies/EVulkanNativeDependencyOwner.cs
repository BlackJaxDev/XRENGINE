namespace XREngine.Rendering.Vulkan;

/// <summary>Native Vulkan ownership domains that can invalidate recorded artifacts.</summary>
internal enum EVulkanNativeDependencyOwner : byte
{
    PipelineLayout,
    Pipeline,
    DescriptorLayout,
    DescriptorTable,
    RenderPass,
    Output,
    Shader,
    Shadow,
    Probe,
    ResidentVariant,
    CommandArtifact,
}
