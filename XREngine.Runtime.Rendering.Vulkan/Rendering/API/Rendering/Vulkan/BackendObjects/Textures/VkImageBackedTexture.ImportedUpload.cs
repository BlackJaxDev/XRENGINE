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

internal unsafe abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
{
    #region Imported Upload

    internal bool TryCreateSynchronizedImportedUpload(
        in VulkanImportedTextureUploadRequest request,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        ulong publicationToken,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        out VulkanImportedTexturePendingUpload? pendingUpload,
        out string? failureReason)
    {
        pendingUpload = null;
        failureReason = null;

        if (!TryCreateSynchronizedImportedUploadPreparation(
                request,
                new VulkanTextureUploadTicket(0, request.StreamingGeneration),
                residentData,
                includeMipChain,
                publicationToken,
                shouldAcceptResult,
                onFinished,
                onCanceled,
                onError,
                out VulkanImportedTextureUploadPreparation? preparation,
                out failureReason)
            || preparation is null)
        {
            return false;
        }

        bool completed = false;
        try
        {
            while (!completed)
            {
                if (!TryAdvanceSynchronizedImportedUploadPreparation(
                        preparation,
                        out completed,
                        out pendingUpload,
                        out failureReason))
                {
                    return false;
                }
            }

            return pendingUpload is not null;
        }
        finally
        {
            if (!completed || pendingUpload is null)
                ReleaseSynchronizedImportedUploadPreparation(preparation);
        }
    }

    internal bool TryCreateSynchronizedImportedUploadPreparation(
        in VulkanImportedTextureUploadRequest request,
        VulkanTextureUploadTicket ticket,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        ulong publicationToken,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        out VulkanImportedTextureUploadPreparation? preparation,
        out string? failureReason)
    {
        preparation = null;
        failureReason = null;

        if (this is not VkTexture2D texture2D)
        {
            failureReason = "synchronized imported texture uploads are only implemented for XRTexture2D";
            return false;
        }

        if (Data is not XRTexture2D texture)
        {
            failureReason = "texture data is not XRTexture2D";
            return false;
        }

        if (!BackendContext.IsDeviceOperational)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        if (request.CancellationToken.IsCancellationRequested
            || (shouldAcceptResult is not null && !shouldAcceptResult()))
        {
            failureReason = "request was canceled before upload resources were prepared";
            return false;
        }

        XRTexture2D.ApplyResidentDataForVulkanPublication(texture, residentData, includeMipChain);
        RefreshLayout();

        Format format = Format;
        ImageAspectFlags aspectMask = NormalizeAspectMaskForFormat(format, AspectFlags);
        ImageUsageFlags usage = ResolveImportedUploadUsage(format);
        ImageLayout finalLayout = ResolveImportedUploadFinalLayout(usage, format);
        AspectFlags = aspectMask;
        Extent3D extent = _layout.Extent;
        uint mipLevels = Math.Max(_layout.MipLevels, 1u);
        uint arrayLayers = Math.Max(_layout.ArrayLayers, 1u);
        string debugName = BuildImportedUploadDebugName(request, publicationToken);

        preparation = new VulkanImportedTextureUploadPreparation(
            request,
            ticket,
            texture2D,
            residentData,
            includeMipChain,
            publicationToken,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError,
            format,
            aspectMask,
            usage,
            finalLayout,
            extent,
            mipLevels,
            arrayLayers,
            debugName);
        return true;
    }

    internal bool TryAdvanceSynchronizedImportedUploadPreparation(
        VulkanImportedTextureUploadPreparation preparation,
        out bool completed,
        out VulkanImportedTexturePendingUpload? pendingUpload,
        out string? failureReason)
    {
        completed = false;
        pendingUpload = null;
        failureReason = null;

        if (!BackendContext.IsDeviceOperational)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        if (!preparation.ShouldAccept())
        {
            failureReason = "request was canceled before upload resources were prepared";
            return false;
        }

        try
        {
            switch (preparation.Step)
            {
                case VulkanImportedTextureUploadPreparationStep.CreateImage:
                    long allocationStart = TextureRuntimeDiagnostics.StartTiming();
                    bool imageCreated = TryCreateImportedUploadImage(
                            preparation.Extent,
                            preparation.MipLevels,
                            preparation.ArrayLayers,
                            preparation.Format,
                            preparation.Usage,
                            out preparation.Image,
                            out preparation.Memory,
                            out preparation.CommittedBytes,
                            out failureReason);
                    VulkanTextureUploadService.RecordImportedTextureNativeAllocationCpu(
                        TextureRuntimeDiagnostics.ElapsedMilliseconds(allocationStart));
                    if (!imageCreated)
                    {
                        return false;
                    }

                    preparation.Step = VulkanImportedTextureUploadPreparationStep.CreateImageView;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.CreateImageView:
                    preparation.ImageView = CreateImportedUploadImageView(
                        preparation.Image,
                        preparation.Format,
                        preparation.AspectMask,
                        preparation.MipLevels,
                        preparation.ArrayLayers);
                    preparation.Step = CreateSampler
                        ? VulkanImportedTextureUploadPreparationStep.CreateSampler
                        : VulkanImportedTextureUploadPreparationStep.Complete;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.CreateSampler:
                    preparation.Sampler = CreateImportedUploadSampler();
                    preparation.Step = VulkanImportedTextureUploadPreparationStep.Complete;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.Complete:
                    pendingUpload = new VulkanImportedTexturePendingUpload(
                        preparation.Request,
                        preparation.Ticket,
                        preparation.Texture,
                        preparation.Image,
                        preparation.Memory,
                        preparation.ImageView,
                        preparation.Sampler,
                        preparation.Format,
                        preparation.AspectMask,
                        preparation.Usage,
                        preparation.FinalLayout,
                        preparation.Extent,
                        preparation.MipLevels,
                        preparation.ArrayLayers,
                        preparation.CommittedBytes,
                        preparation.PublicationToken,
                        preparation.ResidentData,
                        preparation.IncludeMipChain,
                        preparation.ShouldAcceptResult,
                        preparation.OnFinished,
                        preparation.OnCanceled,
                        preparation.OnError);

                    preparation.Image = default;
                    preparation.Memory = default;
                    preparation.ImageView = default;
                    preparation.Sampler = default;
                    completed = true;
                    return true;
            }
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }

        failureReason = $"unknown upload preparation step {preparation.Step}";
        return false;
    }

    /// <summary>
    /// Worker-only chunk staging.  A ticket retains decoded resident data and
    /// destination ownership, but it can hold exactly one bounded native staging
    /// lease until that chunk's graphics fence completes.
    /// </summary>
    internal EVulkanImportedTextureChunkPreparation TryPrepareNextSynchronizedImportedUploadChunk(
        VulkanImportedTexturePendingUpload upload,
        bool foregroundRequired,
        out string? failureReason)
    {
        failureReason = null;
        if (upload.StagingResources.Length != 0)
        {
            failureReason = "imported texture ticket already owns an in-flight staging chunk";
            return EVulkanImportedTextureChunkPreparation.Failed;
        }

        uint levelCount = Math.Min((uint)upload.ResidentData.Mipmaps.Length, upload.MipLevels);
        int nextMipLevel = upload.NextMipLevel;
        uint firstRow = upload.NextMipRow;

        while (nextMipLevel < levelCount)
        {
            uint level = (uint)nextMipLevel;
            Mipmap2D? mip = upload.ResidentData.Mipmaps[level];
            if (mip is null)
            {
                nextMipLevel++;
                firstRow = 0;
                continue;
            }

            DataSource? uploadData = VkFormatConversions.CreateNormalizedUploadData2D(mip, upload.Format, out bool ownsUploadData);
            try
            {
                Extent3D mipExtent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1u);
                if (uploadData is null || uploadData.Length == 0)
                {
                    failureReason = $"mip {level} has no normalized upload bytes";
                    return EVulkanImportedTextureChunkPreparation.Failed;
                }

                uint rowCount = Math.Max(mipExtent.Height, 1u);
                if ((ulong)uploadData.Length % rowCount != 0)
                {
                    failureReason = $"normalized mip {level} byte count {uploadData.Length:N0} cannot be divided into {rowCount} rows for bounded staging";
                    return EVulkanImportedTextureChunkPreparation.Failed;
                }

                ulong bytesPerRow = (ulong)uploadData.Length / rowCount;
                if (bytesPerRow == 0 || bytesPerRow > VulkanStagingManager.ForegroundChunkCapacity)
                {
                    failureReason = $"normalized mip {level} row size {bytesPerRow:N0} exceeds the {VulkanStagingManager.ForegroundChunkCapacity:N0}-byte foreground staging chunk";
                    return EVulkanImportedTextureChunkPreparation.Failed;
                }

                if (firstRow >= rowCount)
                {
                    nextMipLevel++;
                    firstRow = 0;
                    continue;
                }

                uint rowsPerChunk = (uint)Math.Max(1UL, VulkanStagingManager.ForegroundChunkCapacity / bytesPerRow);
                uint chunkRows = Math.Min(rowsPerChunk, rowCount - firstRow);
                ulong chunkBytes = bytesPerRow * chunkRows;
                long stagingCopyStart = TextureRuntimeDiagnostics.StartTiming();
                bool stagingAllocated = TryAllocateImportedStagingBuffer(
                        new DataSource(uploadData.Address + (long)(firstRow * bytesPerRow), checked((uint)chunkBytes)),
                        out Buffer stagingBuffer,
                        out DeviceMemory stagingMemory,
                        foregroundRequired);
                VulkanTextureUploadService.RecordImportedTextureStagingCopyCpu(
                    TextureRuntimeDiagnostics.ElapsedMilliseconds(stagingCopyStart));
                if (!stagingAllocated)
                {
                    failureReason = $"bounded staging lease is unavailable for mip {level}, rows {firstRow}-{firstRow + chunkRows - 1}";
                    return EVulkanImportedTextureChunkPreparation.Deferred;
                }

                BufferImageCopy region = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = upload.AspectMask,
                        MipLevel = level,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D(0, (int)firstRow, 0),
                    ImageExtent = new Extent3D(mipExtent.Width, chunkRows, 1u),
                };

                uint nextRow = firstRow + chunkRows;
                int followingMip = nextMipLevel;
                uint followingRow = nextRow;
                if (followingRow >= rowCount)
                {
                    followingMip++;
                    followingRow = 0;
                }

                bool final = !HasRemainingImportedUploadRows(upload, followingMip, followingRow);
                upload.SetPreparedChunk(
                    new VulkanImportedTextureUploadStagingResource(
                        default,
                        stagingBuffer,
                        stagingMemory,
                        region,
                        chunkBytes,
                        BackendContext.Resources.GetPublishedGeneration(ObjectType.Buffer, stagingBuffer.Handle)),
                    followingMip,
                    followingRow,
                    final);
                return EVulkanImportedTextureChunkPreparation.Prepared;
            }
            finally
            {
                if (ownsUploadData)
                    uploadData?.Dispose();
            }
        }

        failureReason = "resident data did not produce any staging uploads";
        return EVulkanImportedTextureChunkPreparation.Failed;
    }

    private static bool HasRemainingImportedUploadRows(
        VulkanImportedTexturePendingUpload upload,
        int nextMipLevel,
        uint nextMipRow)
    {
        uint levelCount = Math.Min((uint)upload.ResidentData.Mipmaps.Length, upload.MipLevels);
        for (int index = nextMipLevel; index < levelCount; index++)
        {
            Mipmap2D? mip = upload.ResidentData.Mipmaps[index];
            if (mip is null)
                continue;
            uint rows = Math.Max(mip.Height, 1u);
            if (index != nextMipLevel || nextMipRow < rows)
                return true;
        }

        return false;
    }

    internal void ReleaseSynchronizedImportedUploadPreparation(VulkanImportedTextureUploadPreparation preparation)
    {
        if (preparation.Image.Handle == 0
            && preparation.Memory.Handle == 0
            && preparation.ImageView.Handle == 0
            && preparation.Sampler.Handle == 0
            && preparation.StagingResources.Count == 0)
        {
            return;
        }

        ReleasePreparedImportedUploadResources(
            preparation.Image,
            preparation.Memory,
            preparation.ImageView,
            preparation.Sampler,
            preparation.CommittedBytes,
            [.. preparation.StagingResources]);
        preparation.Image = default;
        preparation.Memory = default;
        preparation.ImageView = default;
        preparation.Sampler = default;
        preparation.StagingResources.Clear();
    }

    private static string BuildImportedUploadDebugName(in VulkanImportedTextureUploadRequest request, ulong publicationToken)
    {
        string textureName = string.IsNullOrWhiteSpace(request.TextureName)
            ? "ImportedTexture"
            : request.TextureName!;
        return $"ImportedTextureUpload.{textureName}.gen{request.StreamingGeneration}.token{publicationToken}";
    }

    private bool TryCreateImportedUploadImage(
        Extent3D extent,
        uint mipLevels,
        uint arrayLayers,
        Format format,
        ImageUsageFlags usage,
        out Image image,
        out DeviceMemory memory,
        out long committedBytes,
        out string? failureReason)
    {
        image = default;
        memory = default;
        committedBytes = 0L;
        failureReason = null;

        if (!BackendContext.IsDeviceOperational)
        {
            failureReason = $"Vulkan device state is {BackendContext.DeviceContext.State}";
            return false;
        }

        uint* uploadQueueFamilies = stackalloc uint[2];
        uint uploadQueueFamilyCount = 0;
        QueueFamilyIndices families = BackendContext.DeviceContext.QueueFamilies;
        if (families.GraphicsFamilyIndex != families.TransferFamilyIndex)
        {
            uint? graphicsFamily = families.GraphicsFamilyIndex;
            uint? transferFamily = families.TransferFamilyIndex;
            if (graphicsFamily.HasValue &&
                transferFamily.HasValue &&
                graphicsFamily.Value != transferFamily.Value)
            {
                uploadQueueFamilies[0] = graphicsFamily.Value;
                uploadQueueFamilies[1] = transferFamily.Value;
                uploadQueueFamilyCount = 2;
            }
        }
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            Flags = AdditionalImageFlags,
            ImageType = TextureImageType,
            Extent = extent,
            MipLevels = mipLevels,
            ArrayLayers = arrayLayers,
            Format = format,
            Tiling = Tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = uploadQueueFamilyCount > 1 ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = uploadQueueFamilyCount,
            PQueueFamilyIndices = uploadQueueFamilyCount > 0 ? uploadQueueFamilies : null,
        };

        Result createResult = BackendContext.Resources.Images.CreateOwnedImage(
            BackendContext,
            ref imageInfo,
            "VkImageBackedTexture.ImportedUpload",
            out image);
        if (createResult != Result.Success || image.Handle == 0)
        {
            image = default;
            failureReason = $"failed to create synchronized imported texture image ({createResult})";
            return false;
        }

        Api!.GetImageMemoryRequirements(Device, image, out MemoryRequirements memRequirements);
        VulkanMemoryAllocation allocation;
        try
        {
            allocation = BackendContext.Resources.Images.AllocateOwnedImageMemory(BackendContext, image, MemoryProperties);
        }
        catch (Exception ex)
        {
            BackendContext.Resources.Images.DestroyUnpublishedOwnedImage(BackendContext, image, "ImportedUpload.AllocationFailure");
            image = default;
            failureReason = ex.Message;
            return false;
        }

        BackendContext.Resources.Images.RegisterOwnedImageAllocation(image, in allocation);
        memory = allocation.Memory;

        Result bindResult = Api!.BindImageMemory(Device, image, allocation.Memory, allocation.Offset);
        if (bindResult != Result.Success)
        {
            BackendContext.Resources.Images.RemoveOwnedImageAllocation(image);
            BackendContext.Resources.Images.DestroyUnpublishedOwnedImage(BackendContext, image, "ImportedUpload.BindFailure");
            BackendContext.Resources.Images.FreeMemory(BackendContext, in allocation);
            image = default;
            memory = default;
            failureReason = $"failed to bind synchronized imported texture image memory ({bindResult})";
            return false;
        }

        committedBytes = (long)memRequirements.Size;
        RuntimeEngine.Rendering.Stats.Vram.AddTextureAllocation(committedBytes);
        return true;
    }

    private ImageUsageFlags ResolveImportedUploadUsage(Format format)
    {
        ImageUsageFlags usage = DefaultUsage;
        if (Data.RequiresStorageUsage)
            usage |= ImageUsageFlags.StorageBit;
        if (VkFormatConversions.IsDepthStencilFormat(format))
        {
            usage &= ~ImageUsageFlags.ColorAttachmentBit;
            usage |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        return usage;
    }

    /// <summary>
    /// Selects the stable post-upload descriptor layout from the exact usage
    /// used to create the image. Sampled/storage images must remain in General;
    /// publishing them as shader-read-only would make the descriptor contract
    /// disagree with the image barrier recorded for the same generation.
    /// </summary>
    private static ImageLayout ResolveImportedUploadFinalLayout(
        ImageUsageFlags usage,
        Format format)
    {
        bool canSample =
            (usage & (ImageUsageFlags.SampledBit |
                      ImageUsageFlags.InputAttachmentBit)) != 0;
        bool canStore = (usage & ImageUsageFlags.StorageBit) != 0;
        if (canStore)
            return ImageLayout.General;
        if (canSample && VkFormatConversions.IsDepthStencilFormat(format))
            return ImageLayout.DepthStencilReadOnlyOptimal;
        if (canSample)
            return ImageLayout.ShaderReadOnlyOptimal;

        return ImageLayout.TransferSrcOptimal;
    }

    private ImageView CreateImportedUploadImageView(
        Image image,
        Format format,
        ImageAspectFlags aspectMask,
        uint mipLevels,
        uint arrayLayers)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = NormalizeImageViewTypeForLayerCount(DefaultViewType, arrayLayers),
            Format = format,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspectMask,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = arrayLayers,
            }
        };

        if (Api!.CreateImageView(Device, ref viewInfo, null, out ImageView created) != Result.Success)
            throw new Exception("Failed to create synchronized imported texture image view.");

        BackendContext.Resources.Images.RegisterView(created, in viewInfo, "VkImageBackedTexture.ImportedUploadView");
        return created;
    }

    private Sampler CreateImportedUploadSampler()
    {
        var (minFilter, magFilter, mipmapMode, uWrap, vWrap, wWrap, lodBias) = ReadSamplerSettingsFromData();
        var (minLod, maxLod) = ResolveSamplerLodRange();
        var (compareEnable, compareOp) = ReadCompareSettingsFromData();

        uint anisotropyEnable = Vk.False;
        float maxAnisotropy = 1f;
        if (BackendContext.Supports(EVulkanDeviceCapability.Anisotropy))
        {
            float requestedAnisotropy = Data is XRTexture2D texture2D ? texture2D.MaxAnisotropy : 1.0f;
            Api!.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties props);
            if (requestedAnisotropy > 1.0f && props.Limits.MaxSamplerAnisotropy > 1f)
            {
                anisotropyEnable = Vk.True;
                maxAnisotropy = MathF.Min(props.Limits.MaxSamplerAnisotropy, requestedAnisotropy);
            }
        }

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = magFilter,
            MinFilter = minFilter,
            AddressModeU = uWrap,
            AddressModeV = vWrap,
            AddressModeW = wWrap,
            AnisotropyEnable = anisotropyEnable,
            MaxAnisotropy = maxAnisotropy,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = Vk.False,
            CompareEnable = compareEnable,
            CompareOp = compareOp,
            MipmapMode = mipmapMode,
            MipLodBias = lodBias,
            MinLod = minLod,
            MaxLod = maxLod,
        };

        if (Api!.CreateSampler(Device, ref samplerInfo, null, out Sampler created) != Result.Success)
            throw new Exception("Failed to create synchronized imported texture sampler.");

        BackendContext.RegisterSampler(created, in samplerInfo, nameof(VkImageBackedTexture<TTexture>));
        return created;
    }

    internal void ReleasePreparedImportedUploadResources(VulkanImportedTexturePendingUpload pendingUpload)
    {
        if (!pendingUpload.TryMarkPreparedResourcesReleased())
            return;

        ReleasePreparedImportedUploadResources(
            pendingUpload.Image,
            pendingUpload.Memory,
            pendingUpload.ImageView,
            pendingUpload.Sampler,
            pendingUpload.CommittedBytes,
            pendingUpload.StagingResources);
        pendingUpload.DetachPublishedImageHandles();
    }

    private void ReleasePreparedImportedUploadResources(
        Image image,
        DeviceMemory memory,
        ImageView imageView,
        Sampler sampler,
        long committedBytes,
        VulkanImportedTextureUploadStagingResource[] stagingResources)
    {
        for (int i = 0; i < stagingResources.Length; i++)
        {
            VulkanImportedTextureUploadStagingResource staging = stagingResources[i];
            if (!staging.Slice.IsValid)
                BackendContext.Resources.Buffers.Retire(staging.Buffer, staging.Memory, "VkImageBackedTexture.ImportedUpload.DisposePreparedResources");
        }

        if (image.Handle != 0 || memory.Handle != 0 || imageView.Handle != 0 || sampler.Handle != 0)
        {
            BackendContext.Resources.Images.RetireOwnedResources(new RetiredImageResources(
                image,
                memory,
                imageView,
                [],
                sampler,
                committedBytes),
                "VkImageBackedTexture.ImportedUpload.DisposePreparedResources");
        }

        if (committedBytes > 0)
            RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(committedBytes);
    }

    internal EVulkanTextureDescriptorPublicationDisposition
        PublishSynchronizedImportedTextureUpload(
            VulkanImportedTexturePendingUpload pendingUpload,
            bool requireExactDescriptorPublication,
            out string? failureDetail)
    {
        lock (_imageStateLock)
        {
            return PublishSynchronizedImportedTextureUploadLocked(
                pendingUpload,
                requireExactDescriptorPublication,
                out failureDetail);
        }
    }

    private EVulkanTextureDescriptorPublicationDisposition
        PublishSynchronizedImportedTextureUploadLocked(
            VulkanImportedTexturePendingUpload pendingUpload,
            bool requireExactDescriptorPublication,
            out string? failureDetail)
    {
        if (!ReferenceEquals(pendingUpload.Texture, this))
            throw new InvalidOperationException("Imported texture upload publication target does not match the prepared texture wrapper.");

        // Stage every allocation and activation step before the descriptor
        // commit. After an ExactPublished result, only non-throwing field
        // ownership transfers are allowed before the pending upload detaches.
        ImageView[] retiredAttachmentViews;
        if (_attachmentViews.Count > 0)
        {
            retiredAttachmentViews = new ImageView[_attachmentViews.Count];
            int index = 0;
            foreach ((_, ImageView attachmentView) in _attachmentViews)
                retiredAttachmentViews[index++] = attachmentView;
        }
        else
        {
            retiredAttachmentViews = [];
        }

        RetiredImageResources previousResources = new(
            _ownsImageMemory ? _image : default,
            _ownsImageMemory ? _memory : default,
            _view,
            retiredAttachmentViews,
            _sampler,
            _ownsImageMemory ? _allocatedVRAMBytes : 0);
        TextureLayout publishedLayout = new(
            pendingUpload.Extent,
            Math.Max(pendingUpload.ArrayLayers, 1u),
            Math.Max(pendingUpload.MipLevels, 1u));
        if (!IsActive)
        {
            PreGenerated();
            uint bindingId = CacheObject(this);
            _bindingId = bindingId;
            try
            {
                PostGenerated();
            }
            catch
            {
                BackendContext.Resources.BackendObjects.Remove<TTexture>(
                    bindingId);
                BackendContext.Resources.BackendObjects.Remove(Data);
                _bindingId = null;
                throw;
            }
        }

        ulong publishedWrapperDescriptorGeneration =
            unchecked(DescriptorGeneration + 1UL);
        EVulkanTextureDescriptorPublicationDisposition descriptorPublication =
            BackendContext.Resources.Descriptors.PublishGlobalMaterialTextureDescriptor(
                Data,
                pendingUpload.ImageView,
                pendingUpload.Sampler,
                pendingUpload.FinalLayout,
                pendingUpload.Request.StreamingGeneration,
                publishedWrapperDescriptorGeneration,
                out VulkanBindlessMaterialTextureSlotTransfer retainedResourceTransfer,
                out failureDetail);
        if (descriptorPublication ==
                EVulkanTextureDescriptorPublicationDisposition.Failed ||
            requireExactDescriptorPublication && descriptorPublication !=
                EVulkanTextureDescriptorPublicationDisposition.ExactPublished)
        {
            return descriptorPublication;
        }

        _image = pendingUpload.Image;
        _memory = pendingUpload.Memory;
        _view = pendingUpload.ImageView;
        _sampler = pendingUpload.Sampler;
        _ownsImageMemory = true;
        _physicalGroup = null;
        _extentOverride = null;
        _formatOverride = null;
        _arrayLayersOverride = null;
        _mipLevelsOverride = null;
        _samplesOverride = null;
        _allocatedVRAMBytes = pendingUpload.CommittedBytes;
        _publishedStreamingGeneration =
            pendingUpload.Request.StreamingGeneration;
        _layout = publishedLayout;
        _imageStorageLayout = _layout;
        _imageStorageFormat = pendingUpload.Format;
        _layoutInitialized = true;
        Format = pendingUpload.Format;
        AspectFlags = pendingUpload.AspectMask;
        Usage = pendingUpload.Usage;
        _attachmentViews.Clear();
        _currentImageLayout = pendingUpload.FinalLayout;
        ResetAttachmentLayoutTracking();
        MarkDescriptorDirty();
        MarkUploaded();

        // From this point cleanup cannot release the newly published handles.
        pendingUpload.DetachPublishedImageHandles();

        bool retainedByImmutableDescriptor =
            retainedResourceTransfer.IsValid &&
            BackendContext.Resources.Descriptors
                .CompleteGlobalMaterialTextureRetainedResourceTransfer(
                    in retainedResourceTransfer,
                    in previousResources);

        try
        {
            if (!retainedByImmutableDescriptor &&
                !retainedResourceTransfer.IsValid)
            {
                BackendContext.Resources.Images.RetireOwnedResources(
                    in previousResources,
                    "VkImageBackedTexture.ImportedUpload.Publish");
                if (previousResources.AllocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(
                        previousResources.AllocatedVRAMBytes);
                }
            }
            else if (!retainedByImmutableDescriptor)
            {
                // The native descriptor already committed. If its ownership
                // transfer receipt cannot be completed, leaking the old
                // allocation until device teardown is safer than destroying a
                // resource an immutable slot may still reference.
                Debug.VulkanWarning(
                    "[Vulkan] Immutable descriptor resource transfer failed " +
                    "for texture '{0}' slot={1} generation={2}; retaining " +
                    "the prior allocation until device teardown.",
                    Data.Name ?? "<unnamed>",
                    retainedResourceTransfer.DescriptorIndex,
                    retainedResourceTransfer.SlotGeneration);
            }
        }
        catch (Exception exception)
        {
            // The new wrapper and descriptor generation are already committed.
            // Never report a false upload failure that would release their
            // handles. Retirement diagnostics remain explicit; device teardown
            // still owns all unreclaimed device objects.
            Debug.VulkanWarning(
                "[Vulkan] Streamed texture generation committed for '{0}', " +
                "but prior-generation retirement could not be queued: {1}",
                Data.Name ?? "<unnamed>",
                exception.Message);
        }

        // Compatible content publication preserves binding identity. Command-chain
        // invalidation deliberately treats this class of update as a no-op.
        return descriptorPublication;
    }

    #endregion
}
