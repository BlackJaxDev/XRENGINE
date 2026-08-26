namespace XREngine.Rendering.Vulkan;

/// <summary>Resource classes that may gate an accepted foreground frame.</summary>
internal enum EVulkanFrameDependencyKind
{
    Pipeline,
    Buffer,
    Texture,
    Descriptor,
    Framebuffer,
    Shadow,
    CommandArtifact,
}
