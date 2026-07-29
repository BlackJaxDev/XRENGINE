namespace XREngine.Rendering;

/// <summary>
/// Texture indirection encoding selected for GPU-addressable material records.
/// </summary>
public enum EAdvancedTextureIndirectionMode
{
    None = 0,
    TextureArray,
    OpenGlBindlessHandles,
    VulkanDescriptorIndexing,
    VulkanDescriptorHeap,
}
