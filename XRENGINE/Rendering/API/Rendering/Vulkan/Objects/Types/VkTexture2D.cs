using System;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a two-dimensional texture (<see cref="XRTexture2D"/>).
    /// This is the most common texture type; it uploads width×height mipmaps into
    /// a single-layer 2-D image.
    /// </summary>
    internal sealed class VkTexture2D(VulkanRenderer api, XRTexture2D data) : VkImageBackedTexture<XRTexture2D>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            // If SmallestAllowedMipmapLevel was explicitly set (below its default of 1000),
            // the texture needs a specific mip chain (e.g. bloom). Use SmallestMipmapLevel + 1.
            // Otherwise, use the CPU mipmap data count (1 if none — typical for framebuffer textures).
            bool hasExplicitMipRange = Data.SmallestAllowedMipmapLevel < 1000;
            uint mipLevels = hasExplicitMipRange
                ? (uint)Math.Max(1, Data.SmallestMipmapLevel + 1)
                : (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
        }

        protected override void PushTextureData()
        {
            Generate();

            var mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"Texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap2D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                    CopyBufferToImage(stagingBuffer, level, 0, 1, extent, (ulong)(mip.Data?.Length ?? 0));
                }
                finally
                {
                    DestroyStagingBuffer(stagingBuffer, stagingMemory);
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }

        /// <summary>
        /// Uploads raw packed pixel data (e.g. RGB24 video frames) into the texture
        /// via a Vulkan staging buffer, bypassing the engine's mipmap/DataSource path.
        /// <para>
        /// The image is (re-)created if the dimensions have changed, then:
        /// <list type="number">
        ///   <item>A host-visible staging buffer is allocated and filled via memcpy.</item>
        ///   <item>The image is transitioned to <c>TransferDstOptimal</c>.</item>
        ///   <item><c>vkCmdCopyBufferToImage</c> copies from the staging buffer to mip 0.</item>
        ///   <item>The image is transitioned to <c>ShaderReadOnlyOptimal</c>.</item>
        ///   <item>The staging buffer is released.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="pixelData">Raw packed pixel bytes (e.g. RGB24).</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <returns><c>true</c> if the upload succeeded.</returns>
        internal bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height)
        {
            if (pixelData.IsEmpty || width == 0 || height == 0)
                return false;

            // Ensure the image object exists and is the right size.
            // Resize the engine-side texture so that Generate() creates the
            // correct Vulkan image dimensions.
            if (Data.Width != width || Data.Height != height)
                Data.Resize(width, height);

            Generate();

            if (Image.Handle == 0)
                return false;

            ulong requiredBytes = (ulong)pixelData.Length;

            // Allocate a host-visible staging buffer and memcpy the pixel data.
            BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit;
            if (Renderer.SupportsNvCopyMemoryIndirect && Renderer.SupportsBufferDeviceAddress)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;

            (Buffer stagingBuffer, DeviceMemory stagingMemory) = Renderer.CreateBufferRaw(
                requiredBytes,
                usage,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            // Map → memcpy → unmap.
            void* mapped = null;
            if (Api!.MapMemory(Device, stagingMemory, 0, requiredBytes, 0, &mapped) != Result.Success)
            {
                Renderer.DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            fixed (byte* srcPtr = pixelData)
            {
                System.Buffer.MemoryCopy(srcPtr, mapped, (long)requiredBytes, (long)requiredBytes);
            }

            Api.UnmapMemory(Device, stagingMemory);

            // Transition → copy → transition.
            try
            {
                TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

                Extent3D extent = new(width, height, 1);
                CopyBufferToImage(stagingBuffer, 0, 0, 1, extent, requiredBytes);

                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }
            finally
            {
                Renderer.DestroyBuffer(stagingBuffer, stagingMemory);
            }

            return true;
        }
    }
}
