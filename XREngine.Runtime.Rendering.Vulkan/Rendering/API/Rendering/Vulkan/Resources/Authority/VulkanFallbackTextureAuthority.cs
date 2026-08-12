using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the generation-local fallback texture used to complete otherwise-invalid
/// descriptor bindings without retaining the renderer facade.
/// </summary>
internal unsafe sealed class VulkanFallbackTextureAuthority(
    VulkanResourceRuntime resources,
    VulkanFallbackTextureState state)
{
    private const uint LayerCount = 6;
    private const ImageCreateFlags CubeCompatibleFlag = (ImageCreateFlags)0x10;
    private readonly object _gate = new();
    private VulkanBackendObjectContext? _context;
    private VulkanCommandRuntime? _commands;

    internal bool Ready
    {
        get
        {
            lock (_gate)
                return state.Ready;
        }
    }

    internal void Configure(VulkanCommandRuntime commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        VulkanCommandRuntime? current = Interlocked.CompareExchange(ref _commands, commands, null);
        if (current is not null && !ReferenceEquals(current, commands))
            throw new InvalidOperationException("The fallback texture authority already owns a different command runtime.");
    }

    internal void PublishBackendObjectContext(VulkanBackendObjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        VulkanBackendObjectContext? current = Interlocked.CompareExchange(ref _context, context, null);
        if (current is not null && !ReferenceEquals(current, context))
            throw new InvalidOperationException("The fallback texture authority already owns a different backend context.");
    }

    internal DescriptorImageInfo GetImageInfo(
        DescriptorType descriptorType,
        ImageViewType? expectedViewType = null)
    {
        lock (_gate)
        {
            EnsureCreatedNoLock();
            return new DescriptorImageInfo
            {
                ImageLayout = descriptorType == DescriptorType.StorageImage
                    ? ImageLayout.General
                    : ImageLayout.ShaderReadOnlyOptimal,
                ImageView = GetImageViewNoLock(expectedViewType),
                Sampler = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler
                    ? state.Sampler
                    : default,
            };
        }
    }

    internal Sampler GetSampler()
    {
        lock (_gate)
        {
            EnsureCreatedNoLock();
            return state.Sampler;
        }
    }

    /// <summary>Queues the complete fallback generation for safe device-lifetime retirement.</summary>
    internal void RetireAll()
    {
        lock (_gate)
        {
            if (!HasResourcesNoLock())
                return;

            RetireResourcesNoLock();
            ClearStateNoLock();
        }
    }

    private void EnsureCreatedNoLock()
    {
        if (state.Ready)
            return;

        VulkanBackendObjectContext context = RequireContext();
        if (!context.IsDeviceOperational)
            return;

        try
        {
            CreateImageNoLock(context);
            UploadPixelsNoLock(context);
            state.View = CreateViewNoLock(context, ImageViewType.Type2D, 1, "FallbackTexture.View2D");
            CreateSamplerNoLock(context);
            state.Ready = state.Image.Handle != 0 && state.View.Handle != 0 && state.Sampler.Handle != 0;
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to create fallback texture: {exception.Message}");
            if (HasResourcesNoLock())
                RetireResourcesNoLock();
            ClearStateNoLock();
        }
    }

    private void CreateImageNoLock(VulkanBackendObjectContext context)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            Flags = CubeCompatibleFlag,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(1, 1, 1),
            MipLevels = 1,
            ArrayLayers = LayerCount,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        Result result = resources.Images.CreateOwnedImage(
            context,
            ref imageInfo,
            "FallbackTexture.Image",
            out state.Image);
        context.DeviceContext.ObserveNativeResult("vkCreateImage.FallbackTexture", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create image ({result}).");

        VulkanMemoryAllocation allocation = resources.Images.AllocateOwnedImageMemory(
            context,
            state.Image,
            MemoryPropertyFlags.DeviceLocalBit);
        resources.Images.RegisterOwnedImageAllocation(state.Image, in allocation);
        state.Memory = allocation.Memory;
        result = context.Api.BindImageMemory(context.Device, state.Image, state.Memory, allocation.Offset);
        context.DeviceContext.ObserveNativeResult("vkBindImageMemory.FallbackTexture", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to bind image memory ({result}).");
    }

    private void UploadPixelsNoLock(VulkanBackendObjectContext context)
    {
        const ulong pixelSize = 4;
        const ulong uploadSize = pixelSize * LayerCount;
        byte[] pixels = new byte[checked((int)uploadSize)];
        for (uint layer = 0; layer < LayerCount; layer++)
        {
            int offset = (int)(layer * pixelSize);
            pixels[offset] = 255;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = 255;
        }

        (Buffer staging, DeviceMemory stagingMemory) = resources.Buffers.CreateRaw(
            context,
            uploadSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            owner: "FallbackTexture.Staging");
        try
        {
            fixed (byte* pixelsPtr = pixels)
                resources.Buffers.Update(context, staging, stagingMemory, 0, uploadSize, pixelsPtr);
            VulkanCommandRuntime commands = RequireCommands();
            using VulkanSynchronousUploadSession upload = new(
                context.Api,
                context.DeviceContext,
                commands,
                resources,
                "FallbackTexture.Upload");
            RecordUpload(upload.Encoder, upload.CommandBuffer, state.Image, staging);
            upload.CompleteAndWait(state.Image, staging, "FallbackTexture.Upload");
        }
        finally
        {
            resources.Buffers.Destroy(context, staging, stagingMemory, "FallbackTexture.Staging");
        }
    }

    private void CreateSamplerNoLock(VulkanBackendObjectContext context)
    {
        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1f,
            CompareOp = CompareOp.Always,
            BorderColor = BorderColor.FloatOpaqueWhite,
        };
        Result result = context.Api.CreateSampler(context.Device, ref samplerInfo, null, out state.Sampler);
        context.DeviceContext.ObserveNativeResult("vkCreateSampler.FallbackTexture", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create sampler ({result}).");
        resources.Samplers.Register(state.Sampler, in samplerInfo, "FallbackTexture.Sampler");
    }

    private ImageView GetImageViewNoLock(ImageViewType? viewType)
    {
        if (!state.Ready)
            return default;

        return viewType switch
        {
            null or ImageViewType.Type2D => state.View,
            ImageViewType.Type2DArray => GetOrCreateViewNoLock(
                ImageViewType.Type2DArray, ref state.View2DArray, "FallbackTexture.View2DArray"),
            ImageViewType.TypeCube => GetOrCreateViewNoLock(
                ImageViewType.TypeCube, ref state.ViewCube, "FallbackTexture.ViewCube"),
            ImageViewType.TypeCubeArray => GetOrCreateViewNoLock(
                ImageViewType.TypeCubeArray, ref state.ViewCubeArray, "FallbackTexture.ViewCubeArray"),
            _ => default,
        };
    }

    private ImageView GetOrCreateViewNoLock(ImageViewType viewType, ref ImageView view, string owner)
    {
        if (view.Handle == 0)
            view = CreateViewNoLock(RequireContext(), viewType, LayerCount, owner);
        return view;
    }

    private ImageView CreateViewNoLock(
        VulkanBackendObjectContext context,
        ImageViewType viewType,
        uint layerCount,
        string owner)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = state.Image,
            ViewType = viewType,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = layerCount,
            },
        };
        Result result = context.Api.CreateImageView(context.Device, ref viewInfo, null, out ImageView view);
        context.DeviceContext.ObserveNativeResult($"vkCreateImageView.{owner}", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create {viewType} view ({result}).");
        resources.Images.RegisterView(view, in viewInfo, owner);
        return view;
    }

    private static void RecordUpload(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        Image image,
        Buffer staging)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = LayerCount,
        };
        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransfer);

        BufferImageCopy copy = new()
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = LayerCount,
            },
            ImageExtent = new Extent3D(1, 1, 1),
        };
        encoder.CopyBufferToImage(
            commandBuffer,
            staging,
            image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        ImageMemoryBarrier toSampled = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toSampled);
    }

    private bool HasResourcesNoLock()
        => state.Image.Handle != 0 ||
           state.Memory.Handle != 0 ||
           state.View.Handle != 0 ||
           state.View2DArray.Handle != 0 ||
           state.ViewCube.Handle != 0 ||
           state.ViewCubeArray.Handle != 0 ||
           state.Sampler.Handle != 0;

    private void RetireResourcesNoLock()
        => resources.Images.RetireOwnedResources(new RetiredImageResources(
            state.Image,
            state.Memory,
            state.View,
            [state.View2DArray, state.ViewCube, state.ViewCubeArray],
            state.Sampler,
            0),
            "FallbackTexture");

    private void ClearStateNoLock()
    {
        state.Image = default;
        state.Memory = default;
        state.View = default;
        state.View2DArray = default;
        state.ViewCube = default;
        state.ViewCubeArray = default;
        state.Sampler = default;
        state.Ready = false;
    }

    private VulkanBackendObjectContext RequireContext()
        => Volatile.Read(ref _context)
            ?? throw new InvalidOperationException("The fallback texture backend context has not been published.");

    private VulkanCommandRuntime RequireCommands()
        => Volatile.Read(ref _commands)
            ?? throw new InvalidOperationException("The fallback texture command runtime has not been configured.");
}
