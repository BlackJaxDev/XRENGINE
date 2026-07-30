using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mutable descriptor-table slot owned by <see cref="VulkanBindlessMaterialTextureTableState"/>.
/// </summary>
internal struct MaterialTextureDescriptorSlot
{
    public XRTexture? Texture;
    public DescriptorImageInfo ImageInfo;
    public uint Generation;
    public ulong LastUsedFrameId;
    public ulong RetireAfterFrameId;
    public bool Dirty;
    public bool PendingRetirement;
}
