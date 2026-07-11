using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents a material texture descriptor slot, including its associated texture, image info, and usage metadata.
    /// </summary>
    private struct MaterialTextureDescriptorSlot
    {
        /// <summary>
        /// The XRTexture associated with this descriptor slot.
        /// </summary>
        public XRTexture? Texture;
        /// <summary>
        /// The descriptor image info associated with this descriptor slot.
        /// </summary>
        public DescriptorImageInfo ImageInfo;
        /// <summary>
        /// The generation counter for this descriptor slot, used to track updates.
        /// </summary>
        public uint Generation;
        /// <summary>
        /// The frame ID when this descriptor slot was last used.
        /// </summary>
        public ulong LastUsedFrameId;
        /// <summary>
        /// The frame ID after which this descriptor slot should be retired.
        /// </summary>
        public ulong RetireAfterFrameId;
        /// <summary>
        /// Indicates whether this descriptor slot is dirty and needs to be updated.
        /// </summary>
        public bool Dirty;
        /// <summary>
        /// Indicates whether this descriptor slot is pending retirement.
        /// </summary>
        public bool PendingRetirement;
    }
}
