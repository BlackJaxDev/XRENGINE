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

        if (Renderer.IsDeviceLost)
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
        AspectFlags = aspectMask;
        Extent3D extent = _layout.Extent;
        uint mipLevels = Math.Max(_layout.MipLevels, 1u);
        uint arrayLayers = Math.Max(_layout.ArrayLayers, 1u);
        string debugName = BuildImportedUploadDebugName(request, publicationToken);

        preparation = new VulkanImportedTextureUploadPreparation(
            request,
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

        if (Renderer.IsDeviceLost)
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
                    if (!TryCreateImportedUploadImage(
                            preparation.Extent,
                            preparation.MipLevels,
                            preparation.ArrayLayers,
                            preparation.Format,
                            out preparation.Image,
                            out preparation.Memory,
                            out preparation.CommittedBytes,
                            out failureReason))
                    {
                        return false;
                    }

                    Renderer.SetDebugObjectName(ObjectType.Image, preparation.Image.Handle, $"{preparation.DebugName}.Image");
                    Renderer.SetDebugObjectName(ObjectType.DeviceMemory, preparation.Memory.Handle, $"{preparation.DebugName}.Memory");
                    preparation.Step = VulkanImportedTextureUploadPreparationStep.CreateImageView;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.CreateImageView:
                    preparation.ImageView = CreateImportedUploadImageView(
                        preparation.Image,
                        preparation.Format,
                        preparation.AspectMask,
                        preparation.MipLevels,
                        preparation.ArrayLayers);
                    Renderer.SetDebugObjectName(ObjectType.ImageView, preparation.ImageView.Handle, $"{preparation.DebugName}.View");
                    preparation.Step = CreateSampler
                        ? VulkanImportedTextureUploadPreparationStep.CreateSampler
                        : VulkanImportedTextureUploadPreparationStep.CreateNextStagingMip;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.CreateSampler:
                    preparation.Sampler = CreateImportedUploadSampler();
                    Renderer.SetDebugObjectName(ObjectType.Sampler, preparation.Sampler.Handle, $"{preparation.DebugName}.Sampler");
                    preparation.Step = VulkanImportedTextureUploadPreparationStep.CreateNextStagingMip;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.CreateNextStagingMip:
                    if (TryPrepareNextImportedUploadStagingMip(preparation, out failureReason))
                        return true;

                    if (!string.IsNullOrEmpty(failureReason))
                        return false;

                    if (preparation.StagingResources.Count == 0)
                    {
                        failureReason = "resident data did not produce any staging uploads";
                        return false;
                    }

                    preparation.Step = VulkanImportedTextureUploadPreparationStep.Complete;
                    return true;

                case VulkanImportedTextureUploadPreparationStep.Complete:
                    if (!VulkanImportedTextureUploadValidation.TryValidateCopyRegions(
                            preparation.Request.TextureName,
                            preparation.PublicationToken,
                            preparation.Extent,
                            preparation.MipLevels,
                            preparation.ArrayLayers,
                            preparation.StagingResources,
                            out failureReason))
                    {
                        return false;
                    }

                    pendingUpload = new VulkanImportedTexturePendingUpload(
                        preparation.Request,
                        preparation.Texture,
                        preparation.Image,
                        preparation.Memory,
                        preparation.ImageView,
                        preparation.Sampler,
                        preparation.Format,
                        preparation.AspectMask,
                        preparation.Extent,
                        preparation.MipLevels,
                        preparation.ArrayLayers,
                        preparation.CommittedBytes,
                        preparation.PublicationToken,
                        [.. preparation.StagingResources],
                        preparation.ShouldAcceptResult,
                        preparation.OnFinished,
                        preparation.OnCanceled,
                        preparation.OnError);

                    preparation.Image = default;
                    preparation.Memory = default;
                    preparation.ImageView = default;
                    preparation.Sampler = default;
                    preparation.StagingResources.Clear();
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

    private bool TryPrepareNextImportedUploadStagingMip(
        VulkanImportedTextureUploadPreparation preparation,
        out string? failureReason)
    {
        failureReason = null;
        uint levelCount = Math.Min((uint)preparation.ResidentData.Mipmaps.Length, preparation.MipLevels);

        while (preparation.NextMipLevel < levelCount)
        {
            uint level = (uint)preparation.NextMipLevel++;
            Mipmap2D? mip = preparation.ResidentData.Mipmaps[level];
            if (mip is null)
                continue;

            DataSource? uploadData = VkFormatConversions.CreateNormalizedUploadData2D(mip, preparation.Format, out bool ownsUploadData);
            try
            {
                if (!TryCreateStagingBuffer(uploadData, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                {
                    failureReason = $"could not create staging buffer for mip {level}";
                    return false;
                }

                Renderer.SetDebugObjectName(ObjectType.Buffer, stagingBuffer.Handle, $"{preparation.DebugName}.StagingMip{level}");

                Extent3D mipExtent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1u);
                BufferImageCopy region = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = preparation.AspectMask,
                        MipLevel = level,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = mipExtent,
                };

                preparation.StagingResources.Add(new VulkanImportedTextureUploadStagingResource(
                    stagingBuffer,
                    stagingMemory,
                    region,
                    (ulong)(uploadData?.Length ?? 0u)));
                return true;
            }
            finally
            {
                if (ownsUploadData)
                    uploadData?.Dispose();
            }
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
        out Image image,
        out DeviceMemory memory,
        out long committedBytes,
        out string? failureReason)
    {
        image = default;
        memory = default;
        committedBytes = 0L;
        failureReason = null;

        if (!Renderer.DeviceContext.IsOperational)
        {
            failureReason = $"Vulkan device state is {Renderer.DeviceContext.State}";
            return false;
        }

        ImageUsageFlags usage = DefaultUsage;
        if (Data.RequiresStorageUsage)
            usage |= ImageUsageFlags.StorageBit;
        if (VkFormatConversions.IsDepthStencilFormat(format))
        {
            usage &= ~ImageUsageFlags.ColorAttachmentBit;
            usage |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        uint* uploadQueueFamilies = stackalloc uint[2];
        uint uploadQueueFamilyCount = 0;
        if (Renderer.HasDedicatedTextureUploadTransferQueue)
        {
            QueueFamilyIndices families = Renderer.DeviceContext.QueueFamilies;
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

        Result createResult = Renderer.CreateVulkanImageTracked(ref imageInfo, out image, "VkImageBackedTexture.ImportedUpload");
        if (createResult != Result.Success || image.Handle == 0)
        {
            image = default;
            failureReason = $"failed to create synchronized imported texture image ({createResult})";
            return false;
        }

        Renderer.ClearTrackedImageLayouts(image);
        Api!.GetImageMemoryRequirements(Device, image, out MemoryRequirements memRequirements);
        if (!Renderer.TryAllocateImageMemoryWithFallback(
                image,
                MemoryProperties,
                out VulkanMemoryAllocation allocation,
                out string allocationFailure))
        {
            Renderer.DestroyVulkanImageImmediateTracked(image, "ImportedUpload.AllocationFailure");
            image = default;
            failureReason = allocationFailure;
            return false;
        }

        Renderer.ResourceRuntime.Allocations.Images.Allocations[image.Handle] = allocation;
        Renderer.TrackImageAllocation(
            image,
            allocation,
            ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
            "imported-texture-upload",
            extent.Width,
            extent.Height,
            extent.Depth,
            arrayLayers,
            mipLevels,
            format,
            usage,
            SampleCountFlags.Count1Bit);
        memory = allocation.Memory;

        Result bindResult = Api!.BindImageMemory(Device, image, allocation.Memory, allocation.Offset);
        if (bindResult != Result.Success)
        {
            Renderer.ResourceRuntime.Allocations.Images.Allocations.TryRemove(image.Handle, out _);
            Renderer.UntrackImageAllocation(image);
            Renderer.DestroyVulkanImageImmediateTracked(image, "ImportedUpload.BindFailure");
            Renderer.FreeMemoryAllocation(allocation);
            image = default;
            memory = default;
            failureReason = $"failed to bind synchronized imported texture image memory ({bindResult})";
            return false;
        }

        committedBytes = (long)memRequirements.Size;
        RuntimeEngine.Rendering.Stats.Vram.AddTextureAllocation(committedBytes);
        return true;
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

        Renderer.TrackLiveImageView(created, in viewInfo, "VkImageBackedTexture.ImportedUploadView");
        return created;
    }

    private Sampler CreateImportedUploadSampler()
    {
        var (minFilter, magFilter, mipmapMode, uWrap, vWrap, wWrap, lodBias) = ReadSamplerSettingsFromData();
        var (minLod, maxLod) = ResolveSamplerLodRange();
        var (compareEnable, compareOp) = ReadCompareSettingsFromData();

        uint anisotropyEnable = Vk.False;
        float maxAnisotropy = 1f;
        if (Renderer.SamplerAnisotropyEnabled)
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

        Renderer.RegisterLiveSampler(created, in samplerInfo);
        return created;
    }

    internal void ReleasePreparedImportedUploadResources(VulkanImportedTexturePendingUpload pendingUpload)
    {
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
            Renderer.RetireBuffer(staging.Buffer, staging.Memory);
        }

        if (image.Handle != 0 || memory.Handle != 0 || imageView.Handle != 0 || sampler.Handle != 0)
        {
            Renderer.RetireImageResources(new RetiredImageResources(
                image,
                memory,
                imageView,
                [],
                sampler,
                committedBytes));
        }

        if (committedBytes > 0)
            RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(committedBytes);
    }

    internal void PublishSynchronizedImportedTextureUpload(VulkanImportedTexturePendingUpload pendingUpload)
    {
        if (!ReferenceEquals(pendingUpload.Texture, this))
            throw new InvalidOperationException("Imported texture upload publication target does not match the prepared texture wrapper.");

        RetiredImageResources previousResources;
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

        previousResources = new RetiredImageResources(
            _ownsImageMemory ? _image : default,
            _ownsImageMemory ? _memory : default,
            _view,
            retiredAttachmentViews,
            _sampler,
            _ownsImageMemory ? _allocatedVRAMBytes : 0);

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
        _layout = new TextureLayout(
            pendingUpload.Extent,
            Math.Max(pendingUpload.ArrayLayers, 1u),
            Math.Max(pendingUpload.MipLevels, 1u));
        _imageStorageLayout = _layout;
        _imageStorageFormat = pendingUpload.Format;
        _layoutInitialized = true;
        Format = pendingUpload.Format;
        AspectFlags = pendingUpload.AspectMask;
        _attachmentViews.Clear();
        _currentImageLayout = ImageLayout.ShaderReadOnlyOptimal;
        ResetAttachmentLayoutTracking();
        MarkUploaded();
        if (!IsActive)
        {
            PreGenerated();
            _bindingId = CacheObject(this);
            PostGenerated();
        }

        Renderer.RefreshGlobalMaterialTextureDescriptorForPublishedTexture(Data);
        Renderer.RetireImageResources(previousResources);
        if (previousResources.AllocatedVRAMBytes > 0)
            RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(previousResources.AllocatedVRAMBytes);

        pendingUpload.DetachPublishedImageHandles();
        Renderer.NotifyTextureDescriptorPublished(
            $"ImportedTextureUploadPublished texture='{ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName()}' descriptorGeneration={DescriptorGeneration}");
    }

    #endregion
}
