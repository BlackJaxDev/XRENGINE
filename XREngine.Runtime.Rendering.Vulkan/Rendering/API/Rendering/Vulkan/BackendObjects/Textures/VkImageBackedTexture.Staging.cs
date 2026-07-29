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
        #region Staging Buffers

        /// <summary>
        /// Creates a host-visible staging buffer and copies <paramref name="data"/> into it.
        /// When the NV indirect-copy extension is available, the buffer is also given
        /// <see cref="BufferUsageFlags.ShaderDeviceAddressBit"/> for indirect transfer support.
        /// </summary>
        /// <param name="data">Source pixel data to upload.</param>
        /// <param name="buffer">The created staging buffer handle.</param>
        /// <param name="memory">The staging buffer's device memory.</param>
        /// <returns><c>true</c> if the buffer was created; <c>false</c> if <paramref name="data"/> is null or empty.</returns>
        protected bool TryCreateStagingBuffer(DataSource? data, out Buffer buffer, out DeviceMemory memory)
        {
            if (data is null || data.Length == 0)
            {
                buffer = default;
                memory = default;
                return false;
            }

            bool preferIndirectCopy = Renderer.CanUseNvIndirectBufferCopyUploads;
            BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit;
            if (preferIndirectCopy)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;

            (buffer, memory) = Renderer.CreateBuffer(
                data.Length,
                usage,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                data.Address,
                preferIndirectCopy);
            return true;
        }

        /// <summary>
        /// Creates a Vulkan staging buffer and fills it directly from a file via DirectStorage.
        /// Reads file data straight into the mapped Vulkan host-visible memory, eliminating the
        /// intermediate managed byte[] allocation.
        /// <para>
        /// This is the Vulkan equivalent of DirectStorage's D3D12 <c>DestinationBuffer</c>:
        /// since DirectStorage GPU destinations require <c>ID3D12Resource*</c>, Vulkan engines
        /// achieve the same effect by reading into a mapped staging buffer, then issuing
        /// <c>CmdCopyBufferToImage</c> to transfer to device-local memory.
        /// </para>
        /// Use this for pre-cooked binary texture data (DDS, KTX, raw pixel blobs) that
        /// does not require CPU-side decoding.
        /// </summary>
        /// <param name="filePath">Path to the source file.</param>
        /// <param name="offset">Byte offset within the file.</param>
        /// <param name="length">Number of bytes to read.</param>
        /// <param name="buffer">The created staging buffer.</param>
        /// <param name="memory">The staging buffer's device memory.</param>
        /// <returns><c>true</c> if successful; <c>false</c> if the file could not be read.</returns>
        protected bool TryCreateStagingBufferFromFile(
            string filePath, long offset, int length,
            out Buffer buffer, out DeviceMemory memory)
        {
            buffer = default;
            memory = default;

            if (string.IsNullOrWhiteSpace(filePath) || length <= 0)
                return false;

            bool preferIndirectCopy = Renderer.CanUseNvIndirectBufferCopyUploads;
            BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit;
            if (preferIndirectCopy)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;

            // Allocate a host-visible staging buffer WITHOUT copying any data yet.
            (buffer, memory) = Renderer.CreateBufferRaw(
                (ulong)length,
                usage,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                preferIndirectCopy);

            // Map the staging buffer memory.
            void* mappedPtr = null;
            if (!Renderer.TryMapBufferMemory(buffer, memory, 0, (ulong)length, out mappedPtr))
            {
                Renderer.DestroyBuffer(buffer, memory);
                buffer = default;
                memory = default;
                return false;
            }

            try
            {
                // Read file data directly into the mapped staging buffer via DirectStorage.
                // Falls back to RandomAccess I/O if DirectStorage is unavailable.
                RuntimeDirectStorageIO.TryReadInto(filePath, offset, length, mappedPtr);
            }
            catch
            {
                Renderer.UnmapBufferMemory(buffer, memory);
                Renderer.DestroyBuffer(buffer, memory);
                buffer = default;
                memory = default;
                return false;
            }

            Renderer.UnmapBufferMemory(buffer, memory);
            return true;
        }

        /// <summary>
        /// Releases a staging buffer and its associated device memory.
        /// </summary>
        /// <param name="buffer">The staging buffer to destroy.</param>
        /// <param name="memory">The device memory backing the buffer.</param>
        protected void DestroyStagingBuffer(Buffer buffer, DeviceMemory memory)
            => Renderer.DestroyBuffer(buffer, memory);

        #endregion
    }
}
