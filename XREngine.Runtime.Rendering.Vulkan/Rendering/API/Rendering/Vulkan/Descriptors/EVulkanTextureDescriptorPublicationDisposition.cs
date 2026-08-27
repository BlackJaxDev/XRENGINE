namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes whether a streamed image generation reached the exact descriptor
/// slot that could be consumed by an accepted frame.
/// </summary>
internal enum EVulkanTextureDescriptorPublicationDisposition
{
    Failed = 0,
    NotBound = 1,
    ExactPublished = 2,
}
