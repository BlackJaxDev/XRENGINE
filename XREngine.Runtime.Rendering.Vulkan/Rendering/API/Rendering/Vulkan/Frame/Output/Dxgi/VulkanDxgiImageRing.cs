using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Image = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the shared images of one desktop generation on the DXGI present path. Each
/// image is a Direct3D 11 texture that Vulkan imports. The frame loop renders into
/// these images in place of swapchain images.
/// </summary>
/// <remarks>
/// <para>
/// The frame loop leaves an image in <c>PRESENT_SRC_KHR</c>. Before Direct3D reads it,
/// a release command moves the image to <c>GENERAL</c> and gives it to the external
/// queue family. Before Vulkan writes the image again, an acquire command takes it back
/// in <c>PRESENT_SRC_KHR</c>. These commands are recorded once at creation.
/// </para>
/// <para>
/// A generation retires through the normal swapchain retirement flow. The ring is
/// disposed only after the graphics marker and the present fences prove that Vulkan
/// no longer uses its images.
/// </para>
/// </remarks>
internal sealed unsafe class VulkanDxgiImageRing : IDisposable
{
    /// <summary>Number of images in a ring.</summary>
    internal const int ImageCount = 3;

    /// <summary>Marks a desktop <see cref="SwapchainKHR"/> value that stands for a DXGI generation.</summary>
    private const ulong SentinelTag = 0x5844_4749_0000_0000UL;
    private const ulong SentinelTagMask = 0xFFFF_FFFF_0000_0000UL;
    private const uint QueueFamilyExternal = 0xFFFF_FFFEu;
    private const uint SharedResourceReadWrite = 0x8000_0001u;
    private const uint DisposeWaitMilliseconds = 1000;

    private readonly Vk _api;
    private readonly Device _device;
    private readonly VulkanDxgiPresenter _presenter;
    private readonly ID3D11Texture2D*[] _textures = new ID3D11Texture2D*[ImageCount];
    private readonly Image[] _images = new Image[ImageCount];
    private readonly DeviceMemory[] _memories = new DeviceMemory[ImageCount];
    private readonly CommandBuffer[] _releaseCommands = new CommandBuffer[ImageCount];
    private readonly CommandBuffer[] _acquireCommands = new CommandBuffer[ImageCount];
    private readonly ulong[] _copyDoneValues = new ulong[ImageCount];
    private readonly bool[] _releasedToExternal = new bool[ImageCount];
    private CommandPool _commandPool;
    private uint _nextImage;
    private bool _disposed;

    private VulkanDxgiImageRing(
        Vk api,
        Device device,
        VulkanDxgiPresenter presenter,
        Extent2D extent,
        VulkanDxgiSurfaceFormat format,
        ulong generation)
    {
        _api = api;
        _device = device;
        _presenter = presenter;
        Extent = extent;
        SurfaceFormat = format;
        Sentinel = new SwapchainKHR(SentinelTag | (generation & ~SentinelTagMask));
    }

    /// <summary>The image extent of this generation.</summary>
    internal Extent2D Extent { get; }

    /// <summary>The output format of this generation.</summary>
    internal VulkanDxgiSurfaceFormat SurfaceFormat { get; }

    /// <summary>
    /// The value stored in the desktop swapchain field for this generation. It is
    /// never passed to Vulkan.
    /// </summary>
    internal SwapchainKHR Sentinel { get; }

    /// <summary>The Vulkan images, in ring order.</summary>
    internal Image[] Images => _images;

    /// <summary>Returns true when a desktop swapchain value stands for a DXGI generation.</summary>
    internal static bool IsSentinel(SwapchainKHR swapchain)
        => swapchain.Handle != 0 && (swapchain.Handle & SentinelTagMask) == SentinelTag;

    /// <summary>
    /// Creates the shared images of one generation. When this returns false,
    /// <paramref name="reason"/> tells why and no native object remains.
    /// </summary>
    internal static bool TryCreate(
        VulkanDeviceContext device,
        VulkanDxgiPresenter presenter,
        Extent2D extent,
        ulong generation,
        out VulkanDxgiImageRing? ring,
        out string reason)
    {
        ring = null;
        uint graphicsFamily = device.QueueFamilies.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("The Vulkan device has no graphics queue family.");
        VulkanDxgiImageRing created = new(device.Api, device.Device, presenter, extent, presenter.SurfaceFormat, generation);
        try
        {
            for (int index = 0; index < ImageCount; index++)
            {
                if (!created.TryCreateImage(device, index, out reason))
                {
                    created.Dispose();
                    return false;
                }
            }

            if (!created.TryRecordOwnershipCommands(graphicsFamily, out reason))
            {
                created.Dispose();
                return false;
            }
        }
        catch (Exception exception)
        {
            created.Dispose();
            reason = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }

        ring = created;
        reason = string.Empty;
        return true;
    }

    private bool TryCreateImage(VulkanDeviceContext device, int index, out string reason)
    {
        Texture2DDesc textureDesc = new()
        {
            Width = Extent.Width,
            Height = Extent.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = SurfaceFormat.SharedFormat,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
            CPUAccessFlags = 0,
            MiscFlags = (uint)(ResourceMiscFlag.Shared | ResourceMiscFlag.SharedNthandle),
        };
        ID3D11Texture2D* texture = null;
        int hr = _presenter.D3DDevice->CreateTexture2D(&textureDesc, null, &texture);
        if (hr < 0)
        {
            reason = $"creating shared texture {index} ({SurfaceFormat.Name}) failed (0x{hr:X8})";
            return false;
        }
        _textures[index] = texture;

        Guid resourceId = IDXGIResource1.Guid;
        IDXGIResource1* resource = null;
        hr = texture->QueryInterface(&resourceId, (void**)&resource);
        if (hr < 0)
        {
            reason = $"IDXGIResource1 is not available for shared texture {index} (0x{hr:X8})";
            return false;
        }

        void* sharedHandle = null;
        hr = resource->CreateSharedHandle(null, SharedResourceReadWrite, (char*)null, &sharedHandle);
        resource->Release();
        if (hr < 0)
        {
            reason = $"exporting shared texture {index} failed (0x{hr:X8})";
            return false;
        }

        nint handle = (nint)sharedHandle;
        try
        {
            return TryImportImage(device, index, handle, out reason);
        }
        finally
        {
            // Importing a Win32 handle does not take ownership of it.
            VulkanDxgiWin32.CloseIfValid(ref handle);
        }
    }

    private bool TryImportImage(VulkanDeviceContext device, int index, nint handle, out string reason)
    {
        KhrExternalMemoryWin32 memoryWin32 = device.ExtensionFunctions.KhrExternalMemoryWin32
            ?? throw new InvalidOperationException("VK_KHR_external_memory_win32 functions are not loaded.");

        ExternalMemoryImageCreateInfo externalInfo = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
        };
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            PNext = &externalInfo,
            ImageType = ImageType.Type2D,
            Format = SurfaceFormat.VulkanFormat,
            Extent = new Extent3D(Extent.Width, Extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            // Matches the usage of native desktop swapchain images.
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        Result result = _api.CreateImage(_device, &imageInfo, null, &image);
        if (result != Result.Success)
        {
            reason = $"creating Vulkan image {index} for the shared texture failed ({result})";
            return false;
        }
        _images[index] = image;
        device.SetDebugObjectName(ObjectType.Image, image.Handle, $"DxgiPresent.Image[{index}]");

        MemoryWin32HandlePropertiesKHR handleProperties = new()
        {
            SType = StructureType.MemoryWin32HandlePropertiesKhr,
        };
        result = memoryWin32.GetMemoryWin32HandleProperties(
            _device,
            ExternalMemoryHandleTypeFlags.D3D11TextureBit,
            handle,
            &handleProperties);
        if (result != Result.Success)
        {
            reason = $"querying shared texture {index} memory properties failed ({result})";
            return false;
        }

        MemoryRequirements requirements;
        _api.GetImageMemoryRequirements(_device, image, &requirements);
        uint typeBits = requirements.MemoryTypeBits & handleProperties.MemoryTypeBits;
        if (!TrySelectMemoryType(device, typeBits, out uint memoryTypeIndex))
        {
            reason = $"no Vulkan memory type can import shared texture {index}";
            return false;
        }

        MemoryDedicatedAllocateInfo dedicatedInfo = new()
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Image = image,
        };
        ImportMemoryWin32HandleInfoKHR importInfo = new()
        {
            SType = StructureType.ImportMemoryWin32HandleInfoKhr,
            PNext = &dedicatedInfo,
            HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
            Handle = handle,
        };
        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &importInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryTypeIndex,
        };
        DeviceMemory memory;
        result = _api.AllocateMemory(_device, &allocateInfo, null, &memory);
        if (result != Result.Success)
        {
            reason = $"importing shared texture {index} memory failed ({result})";
            return false;
        }
        _memories[index] = memory;

        result = _api.BindImageMemory(_device, image, memory, 0);
        if (result != Result.Success)
        {
            reason = $"binding shared texture {index} memory failed ({result})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TrySelectMemoryType(VulkanDeviceContext device, uint typeBits, out uint memoryTypeIndex)
    {
        PhysicalDeviceMemoryProperties properties;
        device.Api.GetPhysicalDeviceMemoryProperties(device.PhysicalDevice, &properties);
        uint fallback = uint.MaxValue;
        for (uint index = 0; index < properties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) == 0)
                continue;

            if ((properties.MemoryTypes[(int)index].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
            {
                memoryTypeIndex = index;
                return true;
            }

            if (fallback == uint.MaxValue)
                fallback = index;
        }

        memoryTypeIndex = fallback;
        return fallback != uint.MaxValue;
    }

    private bool TryRecordOwnershipCommands(uint graphicsFamily, out string reason)
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = graphicsFamily,
        };
        CommandPool pool;
        Result result = _api.CreateCommandPool(_device, &poolInfo, null, &pool);
        if (result != Result.Success)
        {
            reason = $"creating the DXGI ownership command pool failed ({result})";
            return false;
        }
        _commandPool = pool;

        CommandBuffer* buffers = stackalloc CommandBuffer[ImageCount * 2];
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = ImageCount * 2,
        };
        result = _api.AllocateCommandBuffers(_device, &allocateInfo, buffers);
        if (result != Result.Success)
        {
            reason = $"allocating the DXGI ownership command buffers failed ({result})";
            return false;
        }

        for (int index = 0; index < ImageCount; index++)
        {
            _releaseCommands[index] = buffers[index];
            _acquireCommands[index] = buffers[ImageCount + index];
            if (!TryRecordBarrier(
                    _releaseCommands[index],
                    _images[index],
                    ImageLayout.PresentSrcKhr,
                    ImageLayout.General,
                    graphicsFamily,
                    QueueFamilyExternal,
                    AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferWriteBit,
                    AccessFlags.None,
                    PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
                    PipelineStageFlags.BottomOfPipeBit,
                    out reason) ||
                !TryRecordBarrier(
                    _acquireCommands[index],
                    _images[index],
                    ImageLayout.General,
                    ImageLayout.PresentSrcKhr,
                    QueueFamilyExternal,
                    graphicsFamily,
                    AccessFlags.None,
                    AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit |
                    AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit,
                    out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryRecordBarrier(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        uint sourceFamily,
        uint destinationFamily,
        AccessFlags sourceAccess,
        AccessFlags destinationAccess,
        PipelineStageFlags sourceStage,
        PipelineStageFlags destinationStage,
        out string reason)
    {
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
        };
        Result result = _api.BeginCommandBuffer(commandBuffer, &beginInfo);
        if (result != Result.Success)
        {
            reason = $"beginning a DXGI ownership command buffer failed ({result})";
            return false;
        }

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = sourceFamily,
            DstQueueFamilyIndex = destinationFamily,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _api.CmdPipelineBarrier(commandBuffer, sourceStage, destinationStage, 0, 0, null, 0, null, 1, &barrier);

        result = _api.EndCommandBuffer(commandBuffer);
        if (result != Result.Success)
        {
            reason = $"ending a DXGI ownership command buffer failed ({result})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Selects the next ring image and waits until Direct3D no longer reads it and the
    /// swapchain accepts another frame. The caller must not hold the queue operation
    /// lease, because this call can block up to <paramref name="timeoutNanoseconds"/>.
    /// </summary>
    internal Result WaitForNextImage(ulong timeoutNanoseconds, out uint imageIndex)
    {
        imageIndex = _nextImage;
        if (_disposed)
            return Result.ErrorOutOfDateKhr;

        return _presenter.WaitForFrameSlot(
            _copyDoneValues[imageIndex],
            VulkanDxgiWin32.ToWaitMilliseconds(timeoutNanoseconds));
    }

    /// <summary>
    /// Submits a command that gives an image from <see cref="WaitForNextImage"/> back to
    /// Vulkan and signals <paramref name="acquireSemaphore"/>. The caller holds the queue
    /// operation lease.
    /// </summary>
    /// <param name="acquireFence">An optional fence that signals with the submission, like the fence of a native acquire.</param>
    internal Result SubmitAcquire(Queue queue, uint imageIndex, VkSemaphore acquireSemaphore, Fence acquireFence = default)
    {
        if (_disposed || imageIndex != _nextImage)
            return Result.ErrorOutOfDateKhr;

        CommandBuffer acquireCommand = _acquireCommands[imageIndex];
        bool takesOwnership = _releasedToExternal[imageIndex];
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = takesOwnership ? 1u : 0u,
            PCommandBuffers = takesOwnership ? &acquireCommand : null,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &acquireSemaphore,
        };
        Result result = _api.QueueSubmit(queue, 1, &submitInfo, acquireFence);
        if (result != Result.Success)
            return result;

        _releasedToExternal[imageIndex] = false;
        _nextImage = (imageIndex + 1) % ImageCount;
        return Result.Success;
    }

    /// <summary>
    /// Submits the release command for an image (it waits for
    /// <paramref name="renderDoneSemaphore"/>, signals the shared fence, and signals
    /// <paramref name="presentFence"/>), then queues the Direct3D copy and present. The
    /// caller holds the queue operation lease.
    /// </summary>
    /// <param name="submitted">True when the Vulkan submission was accepted, so the fence will signal.</param>
    internal Result Present(
        Queue queue,
        uint imageIndex,
        VkSemaphore renderDoneSemaphore,
        Fence presentFence,
        uint syncInterval,
        bool allowTearing,
        out bool submitted)
    {
        submitted = false;
        if (_disposed || imageIndex >= ImageCount)
            return Result.ErrorOutOfDateKhr;

        ulong vulkanDoneValue = _presenter.ReserveVulkanDoneValue();
        ulong ignoredWaitValue = 0;
        TimelineSemaphoreSubmitInfo timelineInfo = new()
        {
            SType = StructureType.TimelineSemaphoreSubmitInfo,
            WaitSemaphoreValueCount = 1,
            PWaitSemaphoreValues = &ignoredWaitValue,
            SignalSemaphoreValueCount = 1,
            PSignalSemaphoreValues = &vulkanDoneValue,
        };
        CommandBuffer releaseCommand = _releaseCommands[imageIndex];
        VkSemaphore vulkanDone = _presenter.VulkanDoneSemaphore;
        PipelineStageFlags waitStage = PipelineStageFlags.AllCommandsBit;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            PNext = &timelineInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderDoneSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &releaseCommand,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &vulkanDone,
        };
        Result result = _api.QueueSubmit(queue, 1, &submitInfo, presentFence);
        if (result != Result.Success)
            return result;

        submitted = true;
        _releasedToExternal[imageIndex] = true;
        int hr = _presenter.Present(
            _textures[imageIndex],
            vulkanDoneValue,
            syncInterval,
            allowTearing,
            out ulong copyDoneValue);
        _copyDoneValues[imageIndex] = copyDoneValue;
        return hr >= 0 ? Result.Success : Result.ErrorOutOfDateKhr;
    }

    /// <summary>
    /// Releases every native object. The retirement flow calls this only after Vulkan
    /// no longer uses the images; the method also waits a bounded time for the last
    /// Direct3D copy.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_presenter.IsDisposed)
        {
            ulong lastCopy = 0;
            for (int index = 0; index < ImageCount; index++)
                lastCopy = Math.Max(lastCopy, _copyDoneValues[index]);
            _presenter.WaitForCopyDone(lastCopy, DisposeWaitMilliseconds);
        }

        if (_commandPool.Handle != 0)
        {
            _api.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }

        for (int index = 0; index < ImageCount; index++)
        {
            if (_images[index].Handle != 0)
            {
                _api.DestroyImage(_device, _images[index], null);
                _images[index] = default;
            }

            if (_memories[index].Handle != 0)
            {
                _api.FreeMemory(_device, _memories[index], null);
                _memories[index] = default;
            }

            if (_textures[index] is not null)
            {
                ((IUnknown*)_textures[index])->Release();
                _textures[index] = null;
            }
        }
    }
}
