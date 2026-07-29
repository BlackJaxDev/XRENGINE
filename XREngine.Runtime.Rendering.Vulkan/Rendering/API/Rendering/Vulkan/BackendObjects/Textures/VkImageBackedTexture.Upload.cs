using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
    {
        /// <summary>
        /// Uploads texture pixel data to the GPU via staging buffers.
        /// The default implementation logs a warning; concrete types override this.
        /// </summary>
        protected virtual void PushTextureData()
        {
            Debug.VulkanWarning($"{GetType().Name} does not implement texture data uploads yet.");
        }

        /// <summary>
        /// Full texture uploads replace the whole mip chain. Recreate an active
        /// dedicated image only when its storage no longer matches the CPU texture.
        /// Reusing compatible storage keeps descriptors and command buffers valid
        /// across the initial generate-then-upload sequence.
        /// </summary>
        protected void RecreateImageForFullTextureDataUpload(string reason)
        {
            _ = reason;
            if (!IsActive || _image.Handle == 0 || Renderer.IsDeviceLost)
                return;

            TextureLayout requestedLayout = NormalizeLayout(DescribeTexture());
            Format requestedFormat = ReadFormatFromData();
            bool canReuseDedicatedStorage =
                _physicalGroup is null &&
                _ownsImageMemory &&
                requestedLayout == _imageStorageLayout &&
                requestedFormat == _imageStorageFormat;
            if (canReuseDedicatedStorage)
                return;

            // Destruction is generation-safe and deferred by exact resource tickets;
            // the replacement must not drain unrelated output families.
            Destroy();
        }

        private void RecordCurrentImageStorageDescription()
        {
            _imageStorageLayout = new TextureLayout(
                ResolvedExtent,
                Math.Max(ResolvedArrayLayers, 1u),
                Math.Max(ResolvedMipLevels, 1u));
            _imageStorageFormat = ResolvedFormat;
        }

        /// <summary>
        /// Generates mipmaps on the GPU. Defaults to <see cref="GenerateMipmapsWithBlit"/>.
        /// </summary>
        protected virtual void GenerateMipmapsGPU()
            => GenerateMipmapsWithBlit();

    }
}
