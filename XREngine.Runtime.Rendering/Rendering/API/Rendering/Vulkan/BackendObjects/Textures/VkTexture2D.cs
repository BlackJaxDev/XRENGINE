using System;
using XREngine.Data;
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
        protected override void LinkTextureData()
        {
            base.LinkTextureData();
            Data.PushMipLevelRequested += PushMipLevel;
            Data.SparseTextureStreamingTransitionRequested += ApplySparseTextureStreamingTransition;
        }

        protected override void UnlinkTextureData()
        {
            Data.SparseTextureStreamingTransitionRequested -= ApplySparseTextureStreamingTransition;
            Data.PushMipLevelRequested -= PushMipLevel;
            base.UnlinkTextureData();
        }

        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            // If SmallestAllowedMipmapLevel was explicitly set (below its default of 1000),
            // the texture needs a specific mip chain (e.g. bloom). Use SmallestMipmapLevel + 1.
            // Otherwise, use the CPU mipmap data count (1 if none — typical for framebuffer textures).
            bool hasExplicitMipRange = Data.SmallestAllowedMipmapLevel < 1000;
            uint sourceMipCount = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            uint mipLevels = Data.RuntimeManagedProgressiveUploadActive && sourceMipCount > 1
                ? sourceMipCount
                : Data.AutoGenerateMipmaps || hasExplicitMipRange
                ? (uint)Math.Max(1, Data.SmallestMipmapLevel + 1)
                : sourceMipCount;
            return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
        }

        protected override void PushTextureData()
        {
            var mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"Texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            RecreateImageForFullTextureDataUpload("full 2D texture data upload");
            Generate();
            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Data.AutoGenerateMipmaps
                ? 1u
                : Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap2D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                DataSource? uploadData = VkFormatConversions.CreateNormalizedUploadData2D(mip, ResolvedFormat, out bool ownsUploadData);
                Buffer stagingBuffer;
                DeviceMemory stagingMemory;
                ulong uploadDataSize = uploadData?.Length ?? 0u;
                try
                {
                    if (!TryCreateStagingBuffer(uploadData, out stagingBuffer, out stagingMemory))
                        continue;
                }
                finally
                {
                    if (ownsUploadData)
                        uploadData?.Dispose();
                }

                try
                {
                    Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                    CopyBufferToImage(stagingBuffer, level, 0, 1, extent, uploadDataSize);
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

        private SparseTextureStreamingTransitionResult ApplySparseTextureStreamingTransition(SparseTextureStreamingTransitionRequest request)
        {
            if (!RuntimeRenderingHostServices.Current.IsRenderThread)
                return SparseTextureStreamingTransitionResult.Unsupported("Vulkan sparse texture transition compatibility must run on the render thread. Use RuntimeRenderingHostServices.TryScheduleSparseTextureStreamingTransitionAsync or ImportedTextureStreamingManager.");

            if (Renderer.IsDeviceLost)
                return SparseTextureStreamingTransitionResult.Unsupported("Vulkan device is lost.");

            if (request.ResidentMipmaps is null || request.ResidentMipmaps.Length == 0)
                return SparseTextureStreamingTransitionResult.Unsupported("Vulkan sparse texture transition compatibility requires resident mip data.");

            Mipmap2D firstMip = request.ResidentMipmaps[0];
            uint logicalWidth = request.LogicalWidth == 0 ? firstMip.Width : request.LogicalWidth;
            uint logicalHeight = request.LogicalHeight == 0 ? firstMip.Height : request.LogicalHeight;
            uint residentMaxDimension = Math.Max(firstMip.Width, firstMip.Height);
            bool includeMipChain = request.ResidentMipmaps.Length > 1;

            Debug.VulkanWarningEvery(
                $"Vulkan.Compat.SparseTextureTransition.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(10),
                "[Vulkan Compat] SparseTextureStreamingTransitionRequested for '{0}' is being satisfied with a dense resident mip upload. Preferred Vulkan path is ImportedTextureStreamingManager/VulkanTextureUploadService; true Vk sparse image page binding is not implemented yet.",
                Data.Name ?? Data.GetDescribingName());

            if (request.PageSelection.Normalize().IsPartial)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Compat.SparseTextureTransition.PartialPages.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(10),
                    "[Vulkan Compat] SparseTextureStreamingTransitionRequested for '{0}' included a partial page selection, but Vulkan dense compatibility uploads whole resident mips. Implement true Vk sparse image page binding for partial residency.",
                    Data.Name ?? Data.GetDescribingName());
            }

            TextureStreamingResidentData residentData = new(
                request.ResidentMipmaps,
                logicalWidth,
                logicalHeight,
                residentMaxDimension);

            try
            {
                XRTexture2D.ApplyResidentDataForVulkanPublication(Data, residentData, includeMipChain);
                Destroy();
                PushTextureData();
                if (IsGenerated)
                    MarkUploaded();
            }
            catch (Exception ex)
            {
                return SparseTextureStreamingTransitionResult.Unsupported(ex.Message);
            }

            long committedBytes = XRTexture2D.EstimateResidentBytes(
                logicalWidth,
                logicalHeight,
                residentMaxDimension,
                request.SizedInternalFormat);

            return new SparseTextureStreamingTransitionResult(
                Applied: true,
                UsedSparseResidency: false,
                RequestedBaseMipLevel: request.RequestedBaseMipLevel,
                CommittedBaseMipLevel: request.RequestedBaseMipLevel,
                NumSparseLevels: 0,
                CommittedBytes: committedBytes,
                ExposureDeferred: false,
                FenceSync: 0,
                FailureReason: null);
        }

        private bool PushMipLevel(int mipIndex)
        {
            if (RuntimeEngine.InvokeOnMainThread(() => PushMipLevel(mipIndex), "VkTexture2D.PushMipLevel"))
                return false;

            if (Renderer.IsDeviceLost)
                return false;

            Mipmap2D[]? mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipIndex < 0 || mipIndex >= mipmaps.Length)
                return true;

            Mipmap2D? mip = mipmaps[mipIndex];
            if (mip is null)
                return true;

            EnsureProgressiveUploadImageCapacity(mipmaps.Length, mipIndex, mip);
            Generate();
            if (Image.Handle == 0)
                return false;

            if ((uint)mipIndex >= ResolvedMipLevels)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.ProgressiveMipOutOfRange.{Data.GetHashCode()}.{mipIndex}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping progressive upload mip {0} for '{1}' because the image has only {2} mip levels.",
                    mipIndex,
                    Data.Name ?? GetDescribingName(),
                    ResolvedMipLevels);
                return true;
            }

            DataSource? uploadData = VkFormatConversions.CreateNormalizedUploadData2D(mip, ResolvedFormat, out bool ownsUploadData);
            Buffer stagingBuffer;
            DeviceMemory stagingMemory;
            ulong uploadDataSize = uploadData?.Length ?? 0u;
            try
            {
                if (!TryCreateStagingBuffer(uploadData, out stagingBuffer, out stagingMemory))
                    return true;
            }
            finally
            {
                if (ownsUploadData)
                    uploadData?.Dispose();
            }

            try
            {
                Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                CopyBufferToImage(stagingBuffer, (uint)mipIndex, 0, 1, extent, uploadDataSize);
                if (!Renderer.IsDeviceLost)
                    TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }
            finally
            {
                DestroyStagingBuffer(stagingBuffer, stagingMemory);
            }

            MarkUploaded();
            return true;
        }

        private void EnsureProgressiveUploadImageCapacity(int sourceMipCount, int mipIndex, Mipmap2D mip)
        {
            if (!Data.RuntimeManagedProgressiveUploadActive || !IsActive)
                return;

            uint requiredMipLevels = sourceMipCount <= 1 ? 1u : (uint)sourceMipCount;
            uint requiredBaseWidth = ExpandMipDimension(mip.Width, mipIndex);
            uint requiredBaseHeight = ExpandMipDimension(mip.Height, mipIndex);
            bool mipCountFits = ResolvedMipLevels >= requiredMipLevels && (uint)mipIndex < ResolvedMipLevels;
            bool extentFits = ResolvedExtent.Width >= requiredBaseWidth && ResolvedExtent.Height >= requiredBaseHeight;

            if (mipCountFits && extentFits)
                return;

            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.ProgressiveImageRecreate.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Recreating progressive texture '{0}' before mip upload: image={1}x{2} mips={3}, requiredBase={4}x{5} mips={6}, uploadMip={7} extent={8}x{9}.",
                Data.Name ?? GetDescribingName(),
                ResolvedExtent.Width,
                ResolvedExtent.Height,
                ResolvedMipLevels,
                requiredBaseWidth,
                requiredBaseHeight,
                requiredMipLevels,
                mipIndex,
                mip.Width,
                mip.Height);
            Destroy();
        }

        private static uint ExpandMipDimension(uint mipDimension, int mipIndex)
        {
            uint dimension = Math.Max(mipDimension, 1u);
            if (mipIndex <= 0)
                return dimension;

            for (int i = 0; i < mipIndex && dimension <= (uint.MaxValue >> 1); i++)
                dimension <<= 1;
            return dimension;
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
            if (Renderer.IsDeviceLost)
                return false;

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
            if (!Renderer.TryMapBufferMemory(stagingBuffer, stagingMemory, 0, requiredBytes, out mapped))
            {
                Renderer.DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            fixed (byte* srcPtr = pixelData)
            {
                System.Buffer.MemoryCopy(srcPtr, mapped, (long)requiredBytes, (long)requiredBytes);
            }

            Renderer.UnmapBufferMemory(stagingBuffer, stagingMemory);

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
