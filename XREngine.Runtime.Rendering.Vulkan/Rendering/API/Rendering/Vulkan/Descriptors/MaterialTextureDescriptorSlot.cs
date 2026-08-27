using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mutable descriptor-table slot owned by <see cref="VulkanBindlessMaterialTextureTableState"/>.
/// </summary>
internal struct MaterialTextureDescriptorSlot
{
    public XRTexture? Texture;
    public DescriptorImageInfo ImageInfo;
    public ImageLayout ExpectedImageLayout;
    public ulong ImageViewGeneration;
    public ulong SamplerGeneration;
    public ulong WrapperDescriptorGeneration;
    public long StreamingGeneration;
    public uint Generation;
    public ulong LastUsedFrameId;
    public ulong RetireAfterFrameId;
    public bool Dirty;
    public bool PendingRetirement;
    /// <summary>
    /// Accepted-frame and ownership-transfer leases. A leased immutable slot
    /// cannot be rewritten, reused, or release the native resources retained
    /// for its descriptor payload.
    /// </summary>
    public int LeaseCount;
    public RetiredImageResources RetainedResources;
    public bool HasRetainedResources;
    /// <summary>
    /// True after this slot's payload has been published. Published generations
    /// are never mutated in place; a replacement generation receives another
    /// unused slot so older material rows and submitted work remain exact.
    /// </summary>
    public bool IsGenerationSnapshot;
}
