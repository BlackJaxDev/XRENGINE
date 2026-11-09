using System;
using XREngine.Data;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan wrapper for a two-dimensional texture (<see cref="XRTexture2D"/>).
/// This is the most common texture type; it uploads widthÃ—height mipmaps into
/// a single-layer 2-D image.
/// </summary>
internal unsafe sealed class VkTexture2D(VulkanBackendObjectContext backendContext, IRenderApiWrapperOwner owner, XRTexture2D data) : VkImageBackedTexture<XRTexture2D>(backendContext, owner, data)
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
        // Otherwise, use the CPU mipmap data count (1 if none â€” typical for framebuffer textures).
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
            try
            {
                Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                _ = UploadStagingDataToImage(uploadData, level, 0, 1, extent);
            }
            finally
            {
                if (ownsUploadData)
                    uploadData?.Dispose();
            }
        }

        if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
            GenerateMipmapsGPU();
        else
            TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
    }

    private SparseTextureStreamingTransitionResult ApplySparseTextureStreamingTransition(SparseTextureStreamingTransitionRequest request)
    {
        if (!RuntimeRenderingHostServices.FrameTiming.IsRenderThread)
            return SparseTextureStreamingTransitionResult.Unsupported("Vulkan sparse texture transition compatibility must run on the render thread. Use RuntimeRenderingHostServices.TryScheduleSparseTextureStreamingTransitionAsync or ImportedTextureStreamingManager.");

        if (!BackendContext.IsDeviceOperational)
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

        if (!BackendContext.IsDeviceOperational)
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
        try
        {
            if (uploadData is null || uploadData.Length == 0)
                return true;

            Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
            _ = UploadStagingDataToImage(uploadData, (uint)mipIndex, 0, 1, extent);
            if (BackendContext.IsDeviceOperational)
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
        finally
        {
            if (ownsUploadData)
                uploadData?.Dispose();
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
    /// Uploads one packed RGB24 video frame to mip 0, bypassing the engine's mipmap/DataSource path.
    /// <para>
    /// The image is (re-)created if the dimensions have changed, then:
    /// <list type="number">
    ///   <item>The RGB24 texels are written to a synchronous frame-data staging slice in the image's byte order.</item>
    ///   <item>The image is transitioned to <c>TransferDstOptimal</c>.</item>
    ///   <item><c>vkCmdCopyBufferToImage</c> copies from the staging slice to mip 0.</item>
    ///   <item>The image is transitioned to <c>ShaderReadOnlyOptimal</c>.</item>
    ///   <item>The texture is marked as uploaded, so descriptor preparation does not push stale CPU mip data over the frame.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <remarks>
    /// Call this on the render thread. Each transfer step is a one-shot graphics-queue
    /// submission that waits for its fence, the same path as other synchronous texture
    /// uploads. The image is therefore in its shader-read layout before the frame that
    /// samples it is submitted, and no in-flight frame command buffer is reused. The
    /// barriers order the copy after earlier queue work that samples the image.
    /// </remarks>
    /// <param name="pixelData">Packed RGB24 texels: <paramref name="width"/> x <paramref name="height"/> x 3 bytes.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="failureReason">The reason for a failed upload, or <c>null</c> when the upload succeeds.</param>
    /// <returns><c>true</c> if the upload succeeded.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the staging arena or a synchronous submission fails.</exception>
    internal bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height, out string? failureReason)
    {
        failureReason = null;
        if (!BackendContext.IsDeviceOperational)
        {
            failureReason = "The Vulkan device is not operational.";
            return false;
        }

        if (width == 0 || height == 0)
        {
            failureReason = $"The video frame size {width}x{height} is not valid.";
            return false;
        }

        ulong expectedSourceBytes = (ulong)width * height * 3UL;
        if ((ulong)pixelData.Length != expectedSourceBytes)
        {
            failureReason = $"The RGB24 video frame has {pixelData.Length} bytes, but {width}x{height} needs {expectedSourceBytes} bytes.";
            return false;
        }

        // Resize the engine-side texture so that Generate() creates the
        // correct Vulkan image dimensions.
        if (Data.Width != width || Data.Height != height)
            Data.Resize(width, height);

        Generate();

        if (Image.Handle == 0)
        {
            failureReason = $"The Vulkan image for texture '{Data.Name ?? GetDescribingName()}' was not created.";
            return false;
        }

        if (ResolvedExtent.Width != width || ResolvedExtent.Height != height)
        {
            failureReason = $"The Vulkan image is {ResolvedExtent.Width}x{ResolvedExtent.Height}, but the video frame is {width}x{height}.";
            return false;
        }

        if (!VulkanRgb24TexelWriter.TryGetDestinationBytesPerTexel(ResolvedFormat, out uint destinationBytesPerTexel))
        {
            failureReason = $"The Vulkan image format {ResolvedFormat} cannot receive RGB24 video frames.";
            return false;
        }

        TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);
        UploadRgb24StagingToImage(pixelData, new Extent3D(width, height, 1), destinationBytesPerTexel);
        TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);

        // A resize invalidates the texture data. This upload replaces the CPU mip path,
        // so publish the new contents instead of letting descriptor preparation push
        // the CPU mip data over the frame.
        MarkUploaded();
        return true;
    }

    /// <summary>
    /// Writes RGB24 texels to a synchronous staging slice in the image's byte order and copies them to mip 0.
    /// </summary>
    private void UploadRgb24StagingToImage(ReadOnlySpan<byte> rgb24PixelData, Extent3D extent, uint destinationBytesPerTexel)
    {
        ulong stagingBytes = (ulong)extent.Width * extent.Height * destinationBytesPerTexel;
        if (stagingBytes > int.MaxValue)
            throw new InvalidOperationException($"The video frame staging upload is too large for the frame-data arena: {stagingBytes:N0} bytes.");

        if (!BackendContext.Resources.TryAcquireSynchronousFrameDataArenaLease(out VulkanSynchronousFrameDataArenaLease lease))
            throw new InvalidOperationException("The Vulkan synchronous frame-data arena is unavailable for a video frame upload.");
        using (lease)
        {
            if (!lease.Arena.TryAllocate(0, EVulkanFrameDataLane.TransferUpload, stagingBytes, 16, out VulkanFrameDataSlice slice))
                throw new InvalidOperationException($"The Vulkan frame-data arena rejected {stagingBytes:N0} video frame staging bytes.");

            if (!lease.Arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope scope))
                throw new InvalidOperationException("The Vulkan frame-data arena could not map the video frame staging slice.");
            using (scope)
            {
                if (!VulkanRgb24TexelWriter.TryWrite(rgb24PixelData, scope.Bytes, ResolvedFormat))
                    throw new InvalidOperationException($"The RGB24 video frame could not be written in the {ResolvedFormat} byte order.");
            }

            CopyBufferToImage(slice, lease, 0, 0, 1, extent);
            if (!lease.TryComplete(slice))
                throw new InvalidOperationException("The Vulkan video frame upload did not complete its frame-data staging slot.");
        }
    }
}
