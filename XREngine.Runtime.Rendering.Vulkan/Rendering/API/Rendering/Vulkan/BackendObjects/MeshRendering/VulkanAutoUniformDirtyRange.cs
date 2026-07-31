namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One coalesced byte range owned by an auto-uniform frequency domain.
/// </summary>
internal readonly record struct VulkanAutoUniformDirtyRange(
    uint Offset,
    uint Size)
{
    internal uint End => checked(Offset + Size);
}
