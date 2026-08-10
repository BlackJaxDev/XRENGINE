using Silk.NET.Vulkan;
using System.Security.Cryptography;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the fixed, engine-allocated image ring used when Vulkan executes
/// without WSI. Acquisition waits only for the selected frame slot.
/// </summary>
internal sealed unsafe class VulkanPresentationlessTargetDriver :
    IVulkanRendererTargetDriver,
    IVulkanExplicitFrameTargetDriver
{
    private readonly RenderTargetOutputProperties _output;
    // This is the target's scoped output capability, never a renderer facade.
    private VulkanTargetOutputContext? _targetContext;
    private VulkanPresentationlessFrameSlot[] _slots = [];
    private bool[] _slotSubmitted = [];
    private bool[] _colorInitialized = [];
    private float _timestampPeriodNanoseconds;
    private int _nextSlot;
    private int _lastSubmittedSlot = -1;
    private bool _deviceLost;

    public VulkanPresentationlessTargetDriver(RendererHostContext hostContext)
    {
        if (hostContext.ExecutionMode is not RenderExecutionMode.Presentationless and not RenderExecutionMode.Component)
            throw new ArgumentOutOfRangeException(
                nameof(hostContext),
                hostContext.ExecutionMode,
                "A presentationless target driver requires a presentationless or component execution mode.");

        _output = hostContext.OutputProperties
            ?? throw new InvalidOperationException("A presentationless Vulkan target requires fixed output properties.");
        _output.Validate();
        ExecutionMode = hostContext.ExecutionMode;
    }

    public RenderExecutionMode ExecutionMode { get; }
    public bool RequiresPresentQueue => false;
    public bool RequiresSwapchainOutput => false;
    public bool SupportsStreamlinePresentation => false;
    public IReadOnlyList<string> RequiredDeviceExtensions => [];
    public RenderTargetOutputProperties OutputProperties => _output;
    public ulong TargetGeneration { get; private set; }
    public bool IsDeviceLost => _deviceLost;
    public double LastCompletedGpuFrameNanoseconds { get; private set; }
    public string PresentationDescription => "Engine-owned presentationless image ring; no Vulkan acquire or present operation.";

    public string[] GetRequiredInstanceExtensions() => [];
    public void CreateInstanceResources(VulkanTargetSurfaceAuthority surfaces) { }

    public void InitializeFinalOutput(VulkanTargetOutputContext output)
    {
        VulkanTargetOutputContext renderer = output;
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.InitializeFinalOutput");
        if (_targetContext is not null)
            throw new InvalidOperationException("The presentationless target generation is already initialized.");

        _targetContext = output;
        Vk api = renderer.VulkanApi;
        api.GetPhysicalDeviceProperties(renderer.PhysicalDevice, out PhysicalDeviceProperties properties);
        _timestampPeriodNanoseconds = Math.Max(properties.Limits.TimestampPeriod, 0.0001f);
        _slots = new VulkanPresentationlessFrameSlot[checked((int)_output.FrameSlotCount)];
        _slotSubmitted = new bool[_slots.Length];
        _colorInitialized = new bool[_slots.Length];
        TargetGeneration = 1;

        try
        {
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = CreateFrameSlot(renderer, i);
        }
        catch
        {
            DestroyFinalOutput(renderer);
            throw;
        }
    }

    public void DestroyFinalOutput(VulkanTargetOutputContext output)
    {
        VulkanTargetOutputContext renderer = output;
        for (int i = 0; i < _slots.Length; i++)
            DestroyFrameSlot(renderer, in _slots[i]);

        _slots = [];
        _slotSubmitted = [];
        _colorInitialized = [];
        _targetContext = null;
        _nextSlot = 0;
        _lastSubmittedSlot = -1;
        TargetGeneration = 0;
    }

    public void DestroyInstanceResources(VulkanTargetSurfaceAuthority surfaces) { }

    public VulkanFrameTargetLease AcquireFrameTarget(out CommandBuffer commandBuffer)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.AcquireFrameTarget");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        int slotIndex = _nextSlot;
        ref VulkanPresentationlessFrameSlot slot = ref _slots[slotIndex];
        _nextSlot = (_nextSlot + 1) % _slots.Length;

        Fence fence = slot.Fence;
        ThrowIfDeviceFailure(api.WaitForFences(device, 1, in fence, true, ulong.MaxValue), "wait for presentationless frame slot");
        renderer.NotifyVulkanFenceCompleted(fence);
        if (_slotSubmitted[slotIndex])
            SampleFrameTimestamp(api, device, in slot);
        ThrowIfDeviceFailure(api.ResetFences(device, 1, in fence), "reset presentationless frame fence");
        Result resetCommandPoolResult = renderer.ResetVulkanCommandPoolTracked(
            slot.CommandPool,
            "Presentationless.AcquireFrameTarget");
        ThrowIfDeviceFailure(resetCommandPoolResult, "reset presentationless command pool");

        try
        {
            CommandBufferBeginInfo begin = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            renderer.ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.Presentationless.Frame");
            ThrowIfDeviceFailure(
                api.BeginCommandBuffer(slot.CommandBuffer, in begin),
                "begin presentationless command buffer");
            api.CmdResetQueryPool(
                slot.CommandBuffer,
                slot.TimestampQueryPool,
                0,
                2);
            api.CmdWriteTimestamp(
                slot.CommandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                slot.TimestampQueryPool,
                0);

            commandBuffer = slot.CommandBuffer;
            VulkanRenderFrameTarget target = new(
                slot.ColorImage,
                slot.ColorView,
                slot.DepthImage,
                slot.DepthView,
                new Extent2D(_output.Width, _output.Height),
                _output.Layers,
                _colorInitialized[slotIndex] ? ImageLayout.TransferSrcOptimal : ImageLayout.Undefined,
                ImageLayout.TransferSrcOptimal,
                TargetGeneration,
                checked((uint)slotIndex));
            return new(
                target,
                VulkanFixedOutputFormatResolver.ResolveColorFormat(
                    _output.ColorFormat),
                VulkanFixedOutputFormatResolver.ResolveDepthFormat(
                    _output.DepthFormat),
                (SampleCountFlags)_output.SampleCount,
                checked((uint)slotIndex),
                Result.Success,
                default,
                0,
                default,
                slot.Fence,
                VulkanFrameTargetCompletionKind.RendererOwned,
                ImagesExternallyOwned: false,
                ViewIndex: 0,
                SupportsHiddenAreaMask: false);
        }
        catch
        {
            RestoreFrameSlotFence(slotIndex);
            throw;
        }
    }

    public void EndFrameRecording(
        in VulkanFrameTargetLease lease,
        CommandBuffer commandBuffer)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.EndFrameRecording");
        int slotIndex = ResolveSlotIndex(in lease);
        VulkanPresentationlessFrameSlot slot = _slots[slotIndex];
        renderer.VulkanApi.CmdWriteTimestamp(
            commandBuffer,
            PipelineStageFlags.BottomOfPipeBit,
            slot.TimestampQueryPool,
            1);
        ThrowIfDeviceFailure(
            renderer.EndCommandBufferTracked(commandBuffer),
            "end presentationless command buffer");
    }

    public void NotifyFrameSubmitted(in VulkanFrameTargetLease lease)
    {
        int slotIndex = ResolveSlotIndex(in lease);
        _slotSubmitted[slotIndex] = true;
        _colorInitialized[slotIndex] = true;
        _lastSubmittedSlot = slotIndex;
    }

    public void CompleteFrameTarget(in VulkanFrameTargetLease lease)
    {
        if (lease.CompletionKind !=
            VulkanFrameTargetCompletionKind.RendererOwned)
        {
            throw new InvalidOperationException(
                $"Presentationless target received unexpected completion kind '{lease.CompletionKind}'.");
        }
    }

    public void AbortFrameTarget(
        in VulkanFrameTargetLease lease,
        bool submissionAccepted)
    {
        if (submissionAccepted || _deviceLost)
            return;

        RestoreFrameSlotFence(ResolveSlotIndex(in lease));
    }

    public byte[] ReadbackLastSubmittedColor(int maxByteCount, ImageLayout sourceLayout)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.ReadbackLastSubmittedColor");
        if (_lastSubmittedSlot < 0)
            throw new InvalidOperationException("No presentationless frame has been submitted.");
        if (_output.SampleCount != 1)
            throw new NotSupportedException("Presentationless readback requires a single-sample final color target. Resolve MSAA output before requesting readback.");

        ref VulkanPresentationlessFrameSlot slot = ref _slots[_lastSubmittedSlot];
        if (slot.ReadbackByteCount > (ulong)Math.Max(maxByteCount, 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxByteCount),
                maxByteCount,
                $"The requested readback bound is smaller than the {slot.ReadbackByteCount}-byte final image.");
        }

        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        Fence fence = slot.Fence;
        ThrowIfDeviceFailure(api.WaitForFences(device, 1, in fence, true, ulong.MaxValue), "wait for presentationless readback source");
        renderer.NotifyVulkanFenceCompleted(fence);
        SampleFrameTimestamp(api, device, in slot);
        ThrowIfDeviceFailure(api.ResetFences(device, 1, in fence), "reset presentationless readback fence");
        Result resetReadbackCommandPoolResult = renderer.ResetVulkanCommandPoolTracked(
            slot.CommandPool,
            "Presentationless.Readback");
        ThrowIfDeviceFailure(
            resetReadbackCommandPoolResult,
            "reset presentationless readback command pool");

        CommandBufferBeginInfo begin = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.Presentationless.Readback");
        ThrowIfDeviceFailure(api.BeginCommandBuffer(slot.CommandBuffer, in begin), "begin presentationless readback command buffer");
        BufferImageCopy copy = new()
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, _output.Layers),
            ImageExtent = new Extent3D(_output.Width, _output.Height, 1),
        };
        api.CmdCopyImageToBuffer(slot.CommandBuffer, slot.ColorImage, sourceLayout, slot.ReadbackBuffer, 1, in copy);
        ThrowIfDeviceFailure(renderer.EndCommandBufferTracked(slot.CommandBuffer), "end presentationless readback command buffer");

        CommandBuffer commandBuffer = slot.CommandBuffer;
        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        // This auxiliary submit contains only the presentationless readback copy and binds no
        // mapped frame-arena mesh data. Scene submissions for this target still flow through
        // SubmitDesktopFrame, which performs the arena flush/ownership transition.
        Result submitResult = renderer.SubmitToQueueTracked(
            renderer.GraphicsQueue,
            ref submit,
            slot.Fence,
            caller: "Presentationless.Readback");
        if (submitResult != Result.Success)
        {
            if (submitResult != Result.ErrorDeviceLost)
                RestoreFrameSlotFence(_lastSubmittedSlot);
            ThrowIfDeviceFailure(submitResult, "submit presentationless readback");
        }
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("vkWaitForFences.Presentationless.Readback");
        ThrowIfDeviceFailure(api.WaitForFences(device, 1, in fence, true, ulong.MaxValue), "wait for presentationless readback");
        renderer.NotifyVulkanFenceCompleted(fence);

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)slot.ReadbackByteCount));
        if (!renderer.TryMapMemoryAllocation(slot.ReadbackAllocation, 0, slot.ReadbackByteCount, out void* mapped))
            throw new InvalidOperationException("The production Vulkan allocator could not map the presentationless readback staging allocation.");
        try
        {
            new ReadOnlySpan<byte>(mapped, bytes.Length).CopyTo(bytes);
        }
        finally
        {
            renderer.UnmapMemoryAllocation(slot.ReadbackAllocation);
        }
        return bytes;
    }

    public string ComputeLastSubmittedColorHash(ImageLayout sourceLayout)
    {
        int byteCount = checked((int)(
            (ulong)_output.Width *
            _output.Height *
            _output.Layers *
            VulkanFixedOutputFormatResolver.BytesPerPixel(_output.ColorFormat)));
        return Convert.ToHexString(SHA256.HashData(ReadbackLastSubmittedColor(byteCount, sourceLayout)));
    }

    private VulkanPresentationlessFrameSlot CreateFrameSlot(VulkanTargetOutputContext renderer, int slotIndex)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.CreateFrameSlot");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        uint graphicsQueueFamily = renderer.GraphicsQueueFamilyIndex;
        CommandPool pool = default;
        Fence fence = default;
        QueryPool timestampQueryPool = default;
        Image color = default;
        ImageView colorView = default;
        VulkanMemoryAllocation colorAllocation = default;
        Image depth = default;
        ImageView depthView = default;
        VulkanMemoryAllocation depthAllocation = default;
        Buffer readbackBuffer = default;
        VulkanMemoryAllocation readbackAllocation = default;
        try
        {
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsQueueFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            ThrowIfDeviceFailure(
                renderer.CreateVulkanCommandPoolTracked(
                    ref poolInfo,
                    out pool,
                    "Presentationless.CommandPool"),
                "create presentationless command pool");
            CommandBufferAllocateInfo allocate = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Result allocateCommandBufferResult = renderer.AllocateVulkanCommandBufferTracked(
                ref allocate,
                out CommandBuffer commandBuffer,
                "Presentationless.CommandBuffer");
            ThrowIfDeviceFailure(
                allocateCommandBufferResult,
                "allocate presentationless command buffer");
            FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            ThrowIfDeviceFailure(api.CreateFence(device, in fenceInfo, null, out fence), "create presentationless frame fence");
            QueryPoolCreateInfo queryInfo = new() { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = 2 };
            ThrowIfDeviceFailure(api.CreateQueryPool(device, in queryInfo, null, out timestampQueryPool), "create presentationless timestamp query pool");

            Format colorFormat = VulkanFixedOutputFormatResolver.ResolveColorFormat(_output.ColorFormat);
            Format depthFormat = VulkanFixedOutputFormatResolver.ResolveDepthFormat(_output.DepthFormat);
            color = CreateImage(
                renderer,
                colorFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                $"Presentationless.Generation{TargetGeneration}.Slot{slotIndex}.Color",
                out colorAllocation);
            colorView = CreateImageView(api, device, color, colorFormat, ImageAspectFlags.ColorBit);
            depth = CreateImage(
                renderer,
                depthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
                $"Presentationless.Generation{TargetGeneration}.Slot{slotIndex}.Depth",
                out depthAllocation);
            depthView = CreateImageView(
                api,
                device,
                depth,
                depthFormat,
                VulkanFixedOutputFormatResolver.DepthAspect(depthFormat));

            ulong readbackByteCount = checked(
                (ulong)_output.Width *
                _output.Height *
                _output.Layers *
                VulkanFixedOutputFormatResolver.BytesPerPixel(_output.ColorFormat));
            readbackBuffer = CreateReadbackBuffer(renderer, readbackByteCount, out readbackAllocation);
            return new(
                pool,
                commandBuffer,
                fence,
                timestampQueryPool,
                color,
                colorView,
                colorAllocation,
                depth,
                depthView,
                depthAllocation,
                readbackBuffer,
                readbackAllocation,
                readbackByteCount);
        }
        catch
        {
            VulkanPresentationlessFrameSlot partial = new(
                pool,
                default,
                fence,
                timestampQueryPool,
                color,
                colorView,
                colorAllocation,
                depth,
                depthView,
                depthAllocation,
                readbackBuffer,
                readbackAllocation,
                0);
            DestroyFrameSlot(renderer, in partial);
            throw;
        }
    }

    private Buffer CreateReadbackBuffer(
        VulkanTargetOutputContext renderer,
        ulong byteCount,
        out VulkanMemoryAllocation allocation)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.CreateReadbackBuffer");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        allocation = default;
        Buffer buffer = default;
        BufferCreateInfo create = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = byteCount,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        try
        {
            ThrowIfDeviceFailure(api.CreateBuffer(device, in create, null, out buffer), "create presentationless readback buffer");
            renderer.TrackLiveBuffer(buffer, "Presentationless.ReadbackBuffer");
            allocation = renderer.AllocateBufferMemoryWithFallback(
                buffer,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            renderer.TrackExternalBufferAllocation(buffer, allocation);
            ThrowIfDeviceFailure(
                api.BindBufferMemory(device, buffer, allocation.Memory, allocation.Offset),
                "bind presentationless readback memory");
            return buffer;
        }
        catch
        {
            if (buffer.Handle != 0)
                renderer.DestroyBufferRaw(buffer, null);
            else
                renderer.FreeMemoryAllocation(allocation);
            allocation = default;
            throw;
        }
    }

    private Image CreateImage(
        VulkanTargetOutputContext renderer,
        Format format,
        ImageUsageFlags usage,
        string owner,
        out VulkanMemoryAllocation allocation)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.CreateImage");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        allocation = default;
        Image image = default;
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(_output.Width, _output.Height, 1),
            MipLevels = 1,
            ArrayLayers = _output.Layers,
            Format = format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            Samples = (SampleCountFlags)_output.SampleCount,
            SharingMode = SharingMode.Exclusive,
        };
        try
        {
            ThrowIfDeviceFailure(renderer.CreateVulkanImageTracked(ref info, out image, owner), "create presentationless image");
            allocation = renderer.AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
            ThrowIfDeviceFailure(
                api.BindImageMemory(device, image, allocation.Memory, allocation.Offset),
                "bind presentationless image memory");
            return image;
        }
        catch
        {
            if (image.Handle != 0)
                renderer.DestroyVulkanImageImmediateTracked(image, $"{owner}.CreateFailure");
            renderer.FreeMemoryAllocation(allocation);
            allocation = default;
            throw;
        }
    }

    private ImageView CreateImageView(
        Vk api,
        Device device,
        Image image,
        Format format,
        ImageAspectFlags aspect)
    {
        RequireTargetContext().ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImageView.Presentationless");
        ImageViewCreateInfo info = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = _output.Layers == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, _output.Layers),
        };
        ThrowIfDeviceFailure(api.CreateImageView(device, in info, null, out ImageView view), "create presentationless image view");
        RequireTargetContext().TrackLiveImageView(view, in info, "Presentationless.ImageView");
        return view;
    }

    private static void DestroyFrameSlot(
        VulkanTargetOutputContext renderer,
        in VulkanPresentationlessFrameSlot slot)
    {
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        if (slot.ColorView.Handle != 0)
        {
            if (renderer.TryBeginDestroyImageView(slot.ColorView, "PresentationlessTarget.ColorView"))
                api.DestroyImageView(device, slot.ColorView, null);
        }
        if (slot.ColorImage.Handle != 0)
            renderer.DestroyVulkanImageImmediateTracked(slot.ColorImage, "PresentationlessTarget.Color");
        renderer.FreeMemoryAllocation(slot.ColorAllocation);
        if (slot.DepthView.Handle != 0)
        {
            if (renderer.TryBeginDestroyImageView(slot.DepthView, "PresentationlessTarget.DepthView"))
                api.DestroyImageView(device, slot.DepthView, null);
        }
        if (slot.DepthImage.Handle != 0)
            renderer.DestroyVulkanImageImmediateTracked(slot.DepthImage, "PresentationlessTarget.Depth");
        renderer.FreeMemoryAllocation(slot.DepthAllocation);
        if (slot.ReadbackBuffer.Handle != 0)
            renderer.DestroyBufferRaw(slot.ReadbackBuffer, null);
        else
            renderer.FreeMemoryAllocation(slot.ReadbackAllocation);
        if (slot.TimestampQueryPool.Handle != 0)
            api.DestroyQueryPool(device, slot.TimestampQueryPool, null);
        if (slot.Fence.Handle != 0)
            api.DestroyFence(device, slot.Fence, null);
        if (slot.CommandPool.Handle != 0)
            renderer.DestroyCommandPoolHostSynchronized(slot.CommandPool);
    }

    private VulkanTargetOutputContext RequireTargetContext()
        => _targetContext
            ?? throw new InvalidOperationException("The presentationless target generation is not initialized.");

    private int ResolveSlotIndex(in VulkanFrameTargetLease lease)
    {
        int slotIndex = checked((int)lease.Target.FrameSlotIndex);
        if ((uint)slotIndex >= (uint)_slots.Length)
        {
            throw new InvalidOperationException(
                $"Presentationless frame lease references invalid slot {slotIndex}.");
        }
        return slotIndex;
    }

    private void RestoreFrameSlotFence(int slotIndex)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("Presentationless.RestoreFrameSlotFence");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        VulkanPresentationlessFrameSlot slot = _slots[slotIndex];
        _ = renderer.ResetVulkanCommandPoolTracked(
            slot.CommandPool,
            "Presentationless.RestoreFrameSlotFence");
        if (slot.Fence.Handle != 0)
            api.DestroyFence(device, slot.Fence, null);

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };
        ThrowIfDeviceFailure(
            api.CreateFence(device, in fenceInfo, null, out Fence replacement),
            "restore presentationless frame-slot fence");
        _slots[slotIndex] = slot with { Fence = replacement };
    }

    private void ThrowIfDeviceFailure(Result result, string operation)
    {
        if (result == Result.Success)
            return;
        if (result == Result.ErrorDeviceLost)
        {
            _deviceLost = true;
            _targetContext?.MarkDeviceLost($"Presentationless target {operation} returned ErrorDeviceLost", operation, result);
        }
        throw new InvalidOperationException($"Vulkan failed to {operation}: {result}.");
    }

    private void SampleFrameTimestamp(
        Vk api,
        Device device,
        in VulkanPresentationlessFrameSlot slot)
    {
        ulong* timestamps = stackalloc ulong[2];
        Result result = api.GetQueryPoolResults(
            device,
            slot.TimestampQueryPool,
            0,
            2,
            (nuint)(sizeof(ulong) * 2),
            timestamps,
            (ulong)sizeof(ulong),
            QueryResultFlags.Result64Bit);
        if (result == Result.Success && timestamps[1] >= timestamps[0])
            LastCompletedGpuFrameNanoseconds = (timestamps[1] - timestamps[0]) * _timestampPeriodNanoseconds;
    }
}
