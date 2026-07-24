using System.Buffers;
using System.Diagnostics;
using ImageMagick;
using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int ScreenshotReadbackRingSize = 8;
    private const ulong MaximumScreenshotReadbackRawBytes = 256UL * 1024UL * 1024UL;
    private const ulong MaximumScreenshotResolveImageBytes = 256UL * 1024UL * 1024UL;
    private static readonly TimeSpan ScreenshotReadbackWatchdogWarningAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScreenshotReadbackFailureAge = TimeSpan.FromSeconds(10);

    private readonly VulkanScreenshotReadbackSlot?[] _screenshotReadbackSlots =
        new VulkanScreenshotReadbackSlot?[ScreenshotReadbackRingSize];
    private int _screenshotReadbackCursor;
    private long _screenshotReadbackReservedRawBytes;
    private long _screenshotReadbackQueuedCount;
    private long _screenshotReadbackCompletedCount;
    private long _screenshotReadbackFailedCount;
    private long _screenshotReadbackRejectedCount;
    private long _screenshotReadbackTimeoutCount;

    public override bool TryQueueScreenshotReadback(
        BoundingRectangle region,
        bool withTransparency,
        Action<ScreenshotReadbackResult> callback,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(callback);
        failure = null;

        if (_deviceLost || !IsDeviceOperational)
            return RejectScreenshotReadback(DeviceLostReason ?? "The Vulkan device is not operational.", out failure);

        if (!RuntimeEngine.IsRenderThread)
        {
            return RejectScreenshotReadback(
                "Vulkan screenshot readbacks must be queued from the render thread so framebuffer and render-graph state remain coherent.",
                out failure);
        }

        PollScreenshotReadbacks();

        using IDisposable? plannerScope = ActiveBoundReadFrameBuffer is null &&
            _lastWindowPresentFrameOpContext is { } context
                ? EnterFrameOpResourcePlannerReadbackScope(in context)
                : null;

        if (!TryResolveScreenshotReadbackSource(
                region,
                out BlitImageInfo source,
                out int sourceX,
                out int sourceY,
                out int width,
                out int height,
                out failure))
        {
            Interlocked.Increment(ref _screenshotReadbackRejectedCount);
            return false;
        }

        uint sourcePixelSize = GetColorFormatPixelSize(source.Format);
        if (sourcePixelSize == 0)
        {
            return RejectScreenshotReadback(
                $"Vulkan screenshot readback does not support source format {source.Format}.",
                out failure);
        }

        ulong rawByteCount;
        try
        {
            rawByteCount = checked((ulong)width * (ulong)height * sourcePixelSize);
        }
        catch (OverflowException)
        {
            return RejectScreenshotReadback("The requested screenshot dimensions overflow the Vulkan readback size calculation.", out failure);
        }

        if (rawByteCount == 0 || rawByteCount > int.MaxValue)
        {
            return RejectScreenshotReadback(
                $"The requested Vulkan screenshot requires {rawByteCount:N0} raw bytes, which is outside the supported per-readback range.",
                out failure);
        }

        if (!TryReserveScreenshotReadbackBytes(rawByteCount))
        {
            return RejectScreenshotReadback(
                $"The bounded Vulkan screenshot queue would exceed its {MaximumScreenshotReadbackRawBytes / (1024 * 1024)} MiB in-flight raw-data budget.",
                out failure);
        }

        bool reservationOwned = true;
        VulkanScreenshotReadbackSlot? slot = AcquireScreenshotReadbackSlot(
            source.Format,
            checked((uint)width),
            checked((uint)height),
            source.Samples != SampleCountFlags.Count1Bit,
            out int slotIndex);
        if (slot is null)
        {
            ReleaseScreenshotReadbackReservation(rawByteCount);
            return RejectScreenshotReadback(
                $"All {ScreenshotReadbackRingSize} bounded Vulkan screenshot readback slots are busy.",
                out failure);
        }

        bool submitted = false;
        try
        {
            PrepareScreenshotReadbackRequest(
                slot,
                callback,
                source.Format,
                width,
                height,
                rawByteCount,
                withTransparency,
                source.Samples != SampleCountFlags.Count1Bit);
            reservationOwned = false;

            if (!EnsureScreenshotReadbackResources(
                    slot,
                    slotIndex,
                    source.Format,
                    checked((uint)width),
                    checked((uint)height),
                    sourcePixelSize,
                    source.Samples != SampleCountFlags.Count1Bit,
                    out failure))
            {
                return RejectPreparedScreenshotReadback(slot, failure ?? "Failed to allocate Vulkan screenshot readback resources.", out failure);
            }

            Result resetFenceResult = Api!.ResetFences(device, 1, in slot.Fence);
            Result resetCommandResult = ResetVulkanCommandBufferTracked(slot.CommandBuffer);
            if (resetFenceResult != Result.Success || resetCommandResult != Result.Success)
            {
                return RejectPreparedScreenshotReadback(
                    slot,
                    $"Failed to reset Vulkan screenshot synchronization resources (fence={resetFenceResult}, command={resetCommandResult}).",
                    out failure);
            }

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Result beginResult = Api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo);
            if (beginResult != Result.Success)
            {
                return RejectPreparedScreenshotReadback(
                    slot,
                    $"vkBeginCommandBuffer failed for screenshot readback ({beginResult}).",
                    out failure);
            }

            ResetCommandBufferBindState(slot.CommandBuffer);
            RecordScreenshotReadbackCommands(
                slot,
                source,
                sourceX,
                sourceY,
                width,
                height);

            Result endResult = Api.EndCommandBuffer(slot.CommandBuffer);
            if (endResult != Result.Success)
            {
                return RejectPreparedScreenshotReadback(
                    slot,
                    $"vkEndCommandBuffer failed for screenshot readback ({endResult}).",
                    out failure);
            }

            CommandBuffer commandBuffer = slot.CommandBuffer;
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            Result submitResult = SubmitToQueueTracked(
                graphicsQueue,
                ref submitInfo,
                slot.Fence,
                caller: "VulkanScreenshotReadback");
            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost("Vulkan screenshot readback submission returned ErrorDeviceLost");

                return RejectPreparedScreenshotReadback(
                    slot,
                    $"Vulkan screenshot readback submission failed ({submitResult}).",
                    out failure);
            }

            slot.SubmittedTimestamp = Stopwatch.GetTimestamp();
            slot.SubmittedAtUtc = DateTimeOffset.UtcNow;
            Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.Submitted);
            submitted = true;
            UpdateReadbackRestoredAttachmentLayout(source, ResolvePostTransferReadLayout(source));
            Interlocked.Increment(ref _screenshotReadbackQueuedCount);
            return true;
        }
        catch (Exception ex)
        {
            failure = $"Failed to queue Vulkan screenshot readback: {ex.Message}";
            Interlocked.Increment(ref _screenshotReadbackRejectedCount);
            return false;
        }
        finally
        {
            if (reservationOwned)
                ReleaseScreenshotReadbackReservation(rawByteCount);

            if (!submitted)
                RecycleUnsubmittedScreenshotReadback(slot);
        }
    }

    public override void PollScreenshotReadbacks()
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueMainThreadTask(
                PollScreenshotReadbacks,
                "VulkanRenderer.PollScreenshotReadbacks",
                RenderThreadJobKind.Readback);
            return;
        }

        if (_deviceLost)
        {
            FailPendingScreenshotReadbacksForDeviceLoss(DeviceLostReason ?? "The Vulkan device was lost.");
            return;
        }

        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            VulkanScreenshotReadbackSlot? slot = _screenshotReadbackSlots[i];
            if (slot is not null &&
                Volatile.Read(ref slot.State) == (int)EVulkanScreenshotReadbackSlotState.Submitted)
            {
                TryConsumeScreenshotReadback(slot, i);
            }
        }
    }

    public override ScreenshotReadbackStatus GetScreenshotReadbackStatus()
    {
        int submitted = 0;
        int cpuProcessing = 0;
        int abandoned = 0;
        double? oldestSubmittedSeconds = null;
        long now = Stopwatch.GetTimestamp();

        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            VulkanScreenshotReadbackSlot? slot = _screenshotReadbackSlots[i];
            if (slot is null)
                continue;

            EVulkanScreenshotReadbackSlotState state =
                (EVulkanScreenshotReadbackSlotState)Volatile.Read(ref slot.State);
            switch (state)
            {
                case EVulkanScreenshotReadbackSlotState.Submitted:
                    submitted++;
                    if (slot.SubmittedTimestamp != 0)
                    {
                        double age = Stopwatch.GetElapsedTime(slot.SubmittedTimestamp, now).TotalSeconds;
                        oldestSubmittedSeconds = oldestSubmittedSeconds.HasValue
                            ? Math.Max(oldestSubmittedSeconds.Value, age)
                            : age;
                    }
                    break;
                case EVulkanScreenshotReadbackSlotState.CpuProcessing:
                    cpuProcessing++;
                    break;
                case EVulkanScreenshotReadbackSlotState.Abandoned:
                    abandoned++;
                    break;
            }
        }

        return new ScreenshotReadbackStatus
        {
            Backend = nameof(VulkanRenderer),
            Supported = true,
            NonBlockingGpuWait = true,
            QueueCapacity = ScreenshotReadbackRingSize,
            SubmittedCount = submitted,
            CpuProcessingCount = cpuProcessing,
            AbandonedCount = abandoned,
            ReservedRawBytes = Math.Max(0L, Interlocked.Read(ref _screenshotReadbackReservedRawBytes)),
            OldestSubmittedSeconds = oldestSubmittedSeconds,
            LifetimeQueuedCount = Interlocked.Read(ref _screenshotReadbackQueuedCount),
            LifetimeCompletedCount = Interlocked.Read(ref _screenshotReadbackCompletedCount),
            LifetimeFailedCount = Interlocked.Read(ref _screenshotReadbackFailedCount),
            LifetimeRejectedCount = Interlocked.Read(ref _screenshotReadbackRejectedCount),
            LifetimeTimeoutCount = Interlocked.Read(ref _screenshotReadbackTimeoutCount),
        };
    }

    private bool TryResolveScreenshotReadbackSource(
        BoundingRectangle region,
        out BlitImageInfo source,
        out int x,
        out int y,
        out int width,
        out int height,
        out string? failure)
    {
        source = default;
        x = 0;
        y = 0;
        width = 0;
        height = 0;
        failure = null;

        XRFrameBuffer? boundReadFrameBuffer = ActiveBoundReadFrameBuffer;
        if (boundReadFrameBuffer is not null)
        {
            ClampReadbackRegion(
                region,
                boundReadFrameBuffer.Width,
                boundReadFrameBuffer.Height,
                out x,
                out y,
                out width,
                out height);
            if (TryResolveBlitImage(
                    boundReadFrameBuffer,
                    _lastPresentedImageIndex,
                    ActiveReadBufferMode,
                    wantColor: true,
                    wantDepth: false,
                    wantStencil: false,
                    out BlitImageInfo boundSource,
                    isSource: true) &&
                TryResolveLiveBlitImage(boundSource, out source) &&
                IsRegionInsideExtent(x, y, width, height, source.Extent))
            {
                return true;
            }

            Debug.VulkanWarningEvery(
                "Vulkan.Readback.AsyncScreenshotBoundFboFailed",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Asynchronous screenshot readback could not resolve bound framebuffer '{0}'; trying the last window-present target.",
                boundReadFrameBuffer.Name ?? "<unnamed>");
        }

        if (_lastWindowPresentFrameBuffer is not null)
        {
            ClampReadbackRegion(
                region,
                _lastWindowPresentFrameBuffer.Width,
                _lastWindowPresentFrameBuffer.Height,
                out x,
                out y,
                out width,
                out height);
            if (TryResolveBlitImage(
                    _lastWindowPresentFrameBuffer,
                    _lastPresentedImageIndex,
                    EReadBufferMode.ColorAttachment0,
                    wantColor: true,
                    wantDepth: false,
                    wantStencil: false,
                    out BlitImageInfo presentSource,
                    isSource: true) &&
                TryResolveLiveBlitImage(presentSource, out source) &&
                IsRegionInsideExtent(x, y, width, height, source.Extent))
            {
                return true;
            }
        }

        if (_lastWindowPresentColorTexture is IFrameBufferAttachement textureAttachment)
        {
            ClampReadbackRegion(
                region,
                textureAttachment.Width,
                textureAttachment.Height,
                out x,
                out y,
                out width,
                out height);
            if (TryResolveTextureBlitImage(
                    _lastWindowPresentColorTexture,
                    mipLevel: 0,
                    layerIndex: 0,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ShaderReadBit,
                    out BlitImageInfo textureSource) &&
                TryResolveLiveBlitImage(textureSource, out source) &&
                IsRegionInsideExtent(x, y, width, height, source.Extent))
            {
                return true;
            }
        }

        failure = "Vulkan could not resolve a live transfer-readable color image for the requested screenshot region. No CPU or OS-window fallback was used.";
        return false;
    }

    private VulkanScreenshotReadbackSlot? AcquireScreenshotReadbackSlot(
        Format format,
        uint width,
        uint height,
        bool needsResolve,
        out int slotIndex)
    {
        slotIndex = -1;

        if (needsResolve)
        {
            for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
            {
                VulkanScreenshotReadbackSlot? compatible = _screenshotReadbackSlots[i];
                if (compatible is null ||
                    compatible.ResolveImage.Handle == 0 ||
                    compatible.ResolveFormat != format ||
                    compatible.ResolveWidth != width ||
                    compatible.ResolveHeight != height)
                {
                    continue;
                }

                if (Interlocked.CompareExchange(
                        ref compatible.State,
                        (int)EVulkanScreenshotReadbackSlotState.Preparing,
                        (int)EVulkanScreenshotReadbackSlotState.Idle) ==
                    (int)EVulkanScreenshotReadbackSlotState.Idle)
                {
                    slotIndex = i;
                    return compatible;
                }
            }
        }

        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            int index = (_screenshotReadbackCursor + i) % _screenshotReadbackSlots.Length;
            VulkanScreenshotReadbackSlot slot = _screenshotReadbackSlots[index] ??=
                new VulkanScreenshotReadbackSlot();
            if (Interlocked.CompareExchange(
                    ref slot.State,
                    (int)EVulkanScreenshotReadbackSlotState.Preparing,
                    (int)EVulkanScreenshotReadbackSlotState.Idle) !=
                (int)EVulkanScreenshotReadbackSlotState.Idle)
            {
                continue;
            }

            _screenshotReadbackCursor = (index + 1) % _screenshotReadbackSlots.Length;
            slotIndex = index;
            return slot;
        }

        return null;
    }

    private void PrepareScreenshotReadbackRequest(
        VulkanScreenshotReadbackSlot slot,
        Action<ScreenshotReadbackResult> callback,
        Format sourceFormat,
        int width,
        int height,
        ulong rawByteCount,
        bool withTransparency,
        bool usedMultisampleResolve)
    {
        slot.Callback = callback;
        slot.SourceFormat = sourceFormat;
        slot.Width = width;
        slot.Height = height;
        slot.RawByteCount = rawByteCount;
        slot.WithTransparency = withTransparency;
        slot.UsedMultisampleResolve = usedMultisampleResolve;
        slot.SubmittedTimestamp = 0;
        slot.FenceSignaledTimestamp = 0;
        slot.SubmittedAtUtc = default;
        Volatile.Write(ref slot.CallbackDelivered, 0);
        Volatile.Write(ref slot.ReservationReleased, 0);
        Volatile.Write(ref slot.WatchdogWarningLogged, 0);
    }

    private bool EnsureScreenshotReadbackResources(
        VulkanScreenshotReadbackSlot slot,
        int slotIndex,
        Format format,
        uint width,
        uint height,
        uint sourcePixelSize,
        bool needsResolve,
        out string? failure)
    {
        failure = null;

        if (slot.CommandBuffer.Handle == 0)
        {
            slot.CommandPool = GetThreadCommandPool();
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = slot.CommandPool,
                CommandBufferCount = 1,
            };
            Result allocateResult = AllocateVulkanCommandBuffersTracked(
                ref allocateInfo,
                out slot.CommandBuffer,
                $"ScreenshotReadback[{slotIndex}]");
            if (allocateResult != Result.Success)
            {
                failure = $"Failed to allocate Vulkan screenshot command buffer ({allocateResult}).";
                return false;
            }
        }

        if (slot.Fence.Handle == 0)
        {
            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };
            Result fenceResult = Api!.CreateFence(device, in fenceCreateInfo, null, out slot.Fence);
            if (fenceResult != Result.Success)
            {
                failure = $"Failed to create Vulkan screenshot fence ({fenceResult}).";
                return false;
            }

            SetDebugObjectName(ObjectType.Fence, slot.Fence.Handle, $"ScreenshotReadback[{slotIndex}].Fence");
        }

        if (slot.StagingBuffer.Handle != 0)
            ReleaseScreenshotReadbackStaging(slot);

        try
        {
            (slot.StagingBuffer, slot.StagingMemory) = CreateReadbackBuffer(slot.RawByteCount);
            SetDebugObjectName(
                ObjectType.Buffer,
                slot.StagingBuffer.Handle,
                $"ScreenshotReadback[{slotIndex}].Staging");
        }
        catch (Exception ex)
        {
            failure = $"Failed to acquire {slot.RawByteCount:N0} bytes of Vulkan screenshot staging memory: {ex.Message}";
            return false;
        }

        if (!needsResolve)
            return true;

        return EnsureScreenshotResolveImage(
            slot,
            slotIndex,
            format,
            width,
            height,
            sourcePixelSize,
            out failure);
    }

    private bool EnsureScreenshotResolveImage(
        VulkanScreenshotReadbackSlot slot,
        int slotIndex,
        Format format,
        uint width,
        uint height,
        uint sourcePixelSize,
        out string? failure)
    {
        failure = null;
        if (slot.ResolveImage.Handle != 0 &&
            slot.ResolveFormat == format &&
            slot.ResolveWidth == width &&
            slot.ResolveHeight == height)
        {
            return true;
        }

        if (slot.ResolveImage.Handle != 0)
            DestroyScreenshotResolveImage(slot, "ScreenshotReadback.ResolveResize");

        ulong requiredBytes = checked((ulong)width * height * sourcePixelSize);
        EvictIdleScreenshotResolveImages(slot, requiredBytes);
        ulong retainedBytes = GetRetainedScreenshotResolveImageBytes();
        if (retainedBytes + requiredBytes > MaximumScreenshotResolveImageBytes)
        {
            failure = $"The Vulkan MSAA screenshot resolve cache would exceed its {MaximumScreenshotResolveImageBytes / (1024 * 1024)} MiB budget.";
            return false;
        }

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Result createResult = CreateVulkanImageTracked(
            ref imageInfo,
            out Image resolveImage,
            $"ScreenshotReadback[{slotIndex}].Resolve");
        if (createResult != Result.Success)
        {
            failure = $"Failed to create Vulkan MSAA screenshot resolve image ({createResult}).";
            return false;
        }

        VulkanMemoryAllocation allocation = default;
        try
        {
            ClearTrackedImageLayouts(resolveImage);
            allocation = AllocateImageMemoryWithFallback(resolveImage, MemoryPropertyFlags.DeviceLocalBit);
            _imageAllocations[resolveImage.Handle] = allocation;
            Result bindResult = Api!.BindImageMemory(device, resolveImage, allocation.Memory, allocation.Offset);
            if (bindResult != Result.Success)
                throw new InvalidOperationException($"vkBindImageMemory returned {bindResult}.");

            slot.ResolveImage = resolveImage;
            slot.ResolveAllocation = allocation;
            slot.ResolveFormat = format;
            slot.ResolveWidth = width;
            slot.ResolveHeight = height;
            slot.ResolveByteCount = requiredBytes;
            SetDebugObjectName(ObjectType.Image, resolveImage.Handle, $"ScreenshotReadback[{slotIndex}].Resolve");
            return true;
        }
        catch (Exception ex)
        {
            _imageAllocations.TryRemove(resolveImage.Handle, out _);
            UntrackImageAllocation(resolveImage);
            DestroyVulkanImageImmediateTracked(resolveImage, "ScreenshotReadback.ResolveCreateFailure");
            if (!allocation.IsNull)
                FreeMemoryAllocation(allocation);
            failure = $"Failed to allocate Vulkan MSAA screenshot resolve image: {ex.Message}";
            return false;
        }
    }

    private void RecordScreenshotReadbackCommands(
        VulkanScreenshotReadbackSlot slot,
        in BlitImageInfo source,
        int sourceX,
        int sourceY,
        int width,
        int height)
    {
        ImageLayout sourceRestoreLayout = ResolvePostTransferReadLayout(source);
        TransitionForBlit(
            slot.CommandBuffer,
            source,
            source.PreferredLayout,
            ImageLayout.TransferSrcOptimal,
            source.AccessMask,
            AccessFlags.TransferReadBit,
            source.StageMask,
            PipelineStageFlags.TransferBit);

        Image copyImage = source.Image;
        ImageLayout copyLayout = ImageLayout.TransferSrcOptimal;
        uint copyMipLevel = source.MipLevel;
        uint copyBaseArrayLayer = source.BaseArrayLayer;
        int copyX = sourceX;
        int copyY = sourceY;

        if (slot.UsedMultisampleResolve)
        {
            if (slot.ResolveImage.Handle == 0)
                throw new InvalidOperationException("The Vulkan screenshot MSAA resolve image was not allocated.");

            BlitImageInfo resolveTarget = new(
                slot.ResolveImage,
                source.Format,
                ImageAspectFlags.ColorBit,
                0,
                1,
                0,
                new Extent2D(checked((uint)width), checked((uint)height)),
                ImageLayout.Undefined,
                PipelineStageFlags.TopOfPipeBit,
                AccessFlags.None,
                samples: SampleCountFlags.Count1Bit);
            TransitionForBlit(
                slot.CommandBuffer,
                resolveTarget,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                AccessFlags.None,
                AccessFlags.TransferWriteBit,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit);

            ImageResolve resolve = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = source.MipLevel,
                    BaseArrayLayer = source.BaseArrayLayer,
                    LayerCount = 1,
                },
                SrcOffset = new Offset3D(sourceX, sourceY, 0),
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                DstOffset = new Offset3D(0, 0, 0),
                Extent = new Extent3D(checked((uint)width), checked((uint)height), 1),
            };
            CmdResolveImageTracked(
                slot.CommandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                slot.ResolveImage,
                ImageLayout.TransferDstOptimal,
                1,
                &resolve);

            TransitionForBlit(
                slot.CommandBuffer,
                resolveTarget,
                ImageLayout.TransferDstOptimal,
                ImageLayout.TransferSrcOptimal,
                AccessFlags.TransferWriteBit,
                AccessFlags.TransferReadBit,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit);

            copyImage = slot.ResolveImage;
            copyMipLevel = 0;
            copyBaseArrayLayer = 0;
            copyX = 0;
            copyY = 0;
        }

        BufferImageCopy copy = new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = copyMipLevel,
                BaseArrayLayer = copyBaseArrayLayer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(copyX, copyY, 0),
            ImageExtent = new Extent3D(checked((uint)width), checked((uint)height), 1),
        };
        CmdCopyImageToBufferTracked(
            slot.CommandBuffer,
            copyImage,
            copyLayout,
            slot.StagingBuffer,
            1,
            &copy);

        TransitionForBlit(
            slot.CommandBuffer,
            source,
            ImageLayout.TransferSrcOptimal,
            sourceRestoreLayout,
            AccessFlags.TransferReadBit,
            source.AccessMask,
            PipelineStageFlags.TransferBit,
            source.StageMask);
    }

    private bool TryConsumeScreenshotReadback(VulkanScreenshotReadbackSlot slot, int slotIndex)
    {
        Result fenceResult = Api!.GetFenceStatus(device, slot.Fence);
        if (fenceResult is Result.NotReady or Result.Timeout)
        {
            ObserveScreenshotReadbackFenceAge(slot, slotIndex);
            return false;
        }

        if (fenceResult != Result.Success)
        {
            string error = $"vkGetFenceStatus failed for Vulkan screenshot readback slot {slotIndex} ({fenceResult}).";
            if (fenceResult == Result.ErrorDeviceLost)
                MarkDeviceLost(error);
            else if (Interlocked.CompareExchange(
                         ref slot.State,
                         (int)EVulkanScreenshotReadbackSlotState.Abandoned,
                         (int)EVulkanScreenshotReadbackSlotState.Submitted) ==
                     (int)EVulkanScreenshotReadbackSlotState.Submitted)
            {
                DeliverScreenshotReadbackFailure(slot, error, gpuCompletionSeconds: null);
                ReleaseScreenshotReadbackReservation(slot);
            }
            return false;
        }

        NotifyVulkanFenceCompleted(slot.Fence);
        slot.FenceSignaledTimestamp = Stopwatch.GetTimestamp();
        double gpuCompletionSeconds = Stopwatch.GetElapsedTime(
            slot.SubmittedTimestamp,
            slot.FenceSignaledTimestamp).TotalSeconds;

        if (Volatile.Read(ref slot.CallbackDelivered) != 0)
        {
            ReleaseScreenshotReadbackStaging(slot);
            ReleaseScreenshotReadbackReservation(slot);
            ClearScreenshotReadbackRequest(slot);
            Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.Idle);
            return true;
        }

        int rawLength = checked((int)slot.RawByteCount);
        byte[] rawPixels = ArrayPool<byte>.Shared.Rent(rawLength);
        bool copied = false;
        try
        {
            if (!TryMapReadbackMemory(
                    slot.StagingBuffer,
                    slot.StagingMemory,
                    0,
                    slot.RawByteCount,
                    out void* mappedPtr))
            {
                DeliverScreenshotReadbackFailure(
                    slot,
                    "Failed to map completed Vulkan screenshot staging memory.",
                    gpuCompletionSeconds);
                return false;
            }

            try
            {
                new ReadOnlySpan<byte>(mappedPtr, rawLength).CopyTo(rawPixels);
                copied = true;
            }
            finally
            {
                UnmapBufferMemory(slot.StagingBuffer, slot.StagingMemory);
            }
        }
        finally
        {
            ReleaseScreenshotReadbackStaging(slot);
            if (!copied)
                ArrayPool<byte>.Shared.Return(rawPixels);
        }

        if (!copied)
        {
            ReleaseScreenshotReadbackReservation(slot);
            ClearScreenshotReadbackRequest(slot);
            Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.Idle);
            return false;
        }

        Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.CpuProcessing);
        Task processingTask = Task.Run(() => ProcessScreenshotReadbackPixels(
            slot,
            slotIndex,
            rawPixels,
            rawLength,
            gpuCompletionSeconds));
        RegisterReadbackTask(processingTask);
        return true;
    }

    private void ObserveScreenshotReadbackFenceAge(VulkanScreenshotReadbackSlot slot, int slotIndex)
    {
        if (slot.SubmittedTimestamp == 0)
            return;

        TimeSpan age = Stopwatch.GetElapsedTime(slot.SubmittedTimestamp);
        if (age >= ScreenshotReadbackWatchdogWarningAge &&
            Interlocked.Exchange(ref slot.WatchdogWarningLogged, 1) == 0)
        {
            Debug.VulkanWarning(
                "[Vulkan] Screenshot readback slot {0} has remained unsignaled for {1:F3}s. The renderer is continuing nonblocking fence polling; no host fence wait or queue-idle wait was issued.",
                slotIndex,
                age.TotalSeconds);
        }

        if (age < ScreenshotReadbackFailureAge || Volatile.Read(ref slot.CallbackDelivered) != 0)
            return;

        Interlocked.Increment(ref _screenshotReadbackTimeoutCount);
        DeliverScreenshotReadbackFailure(
            slot,
            $"The Vulkan screenshot GPU fence did not signal within {ScreenshotReadbackFailureAge.TotalSeconds:F0} seconds. The request failed without blocking the render thread; its submitted resources remain quarantined until the fence signals or the device is destroyed.",
            age.TotalSeconds);
    }

    private void ProcessScreenshotReadbackPixels(
        VulkanScreenshotReadbackSlot slot,
        int slotIndex,
        byte[] rawPixels,
        int rawLength,
        double gpuCompletionSeconds)
    {
        MagickImage? image = null;
        try
        {
            int pixelCount = checked(slot.Width * slot.Height);
            byte[] rgbaPixels = GC.AllocateUninitializedArray<byte>(checked(pixelCount * 4));
            fixed (byte* rawPtr = rawPixels)
            {
                if (!TryConvertColorPixelsToRgba8(rawPtr, slot.SourceFormat, pixelCount, rgbaPixels))
                {
                    DeliverScreenshotReadbackFailure(
                        slot,
                        $"Failed to convert Vulkan screenshot source format {slot.SourceFormat} to RGBA8.",
                        gpuCompletionSeconds);
                    return;
                }
            }

            if (!slot.WithTransparency)
                ForceOpaqueAlpha(rgbaPixels);

            image = new MagickImage(rgbaPixels, new MagickReadSettings
            {
                Width = checked((uint)slot.Width),
                Height = checked((uint)slot.Height),
                Format = MagickFormat.Rgba,
                Depth = 8,
            });

            if (Interlocked.Exchange(ref slot.CallbackDelivered, 1) != 0)
                return;

            DateTimeOffset completedAtUtc = DateTimeOffset.UtcNow;
            double cpuProcessingSeconds = Stopwatch.GetElapsedTime(slot.FenceSignaledTimestamp).TotalSeconds;
            ScreenshotReadbackResult result = ScreenshotReadbackResult.Success(
                image,
                pixelCount,
                slot.Width,
                slot.Height,
                nameof(VulkanRenderer),
                slot.SourceFormat.ToString(),
                rawLength,
                slot.UsedMultisampleResolve,
                slotIndex,
                slot.SubmittedAtUtc,
                completedAtUtc,
                gpuCompletionSeconds,
                cpuProcessingSeconds);
            Action<ScreenshotReadbackResult>? callback = Interlocked.Exchange(ref slot.Callback, null);
            Interlocked.Increment(ref _screenshotReadbackCompletedCount);
            if (callback is null)
                return;

            callback(result);
            image = null;
        }
        catch (Exception ex)
        {
            DeliverScreenshotReadbackFailure(
                slot,
                $"Failed to process Vulkan screenshot readback: {ex.Message}",
                gpuCompletionSeconds);
        }
        finally
        {
            image?.Dispose();
            ArrayPool<byte>.Shared.Return(rawPixels);
            ReleaseScreenshotReadbackReservation(slot);
            ClearScreenshotReadbackRequest(slot);
            if (Volatile.Read(ref slot.State) != (int)EVulkanScreenshotReadbackSlotState.Disposed)
            {
                Volatile.Write(
                    ref slot.State,
                    _deviceLost
                        ? (int)EVulkanScreenshotReadbackSlotState.Abandoned
                        : (int)EVulkanScreenshotReadbackSlotState.Idle);
            }
        }
    }

    private void DeliverScreenshotReadbackFailure(
        VulkanScreenshotReadbackSlot slot,
        string error,
        double? gpuCompletionSeconds)
    {
        if (Interlocked.Exchange(ref slot.CallbackDelivered, 1) != 0)
            return;

        Action<ScreenshotReadbackResult>? callback = Interlocked.Exchange(ref slot.Callback, null);
        Interlocked.Increment(ref _screenshotReadbackFailedCount);
        if (callback is null)
            return;

        try
        {
            callback(ScreenshotReadbackResult.Failure(
                error,
                nameof(VulkanRenderer),
                slot.Width,
                slot.Height,
                slot.SourceFormat.ToString(),
                checked((long)slot.RawByteCount),
                slot.UsedMultisampleResolve,
                Array.IndexOf(_screenshotReadbackSlots, slot),
                slot.SubmittedAtUtc == default ? null : slot.SubmittedAtUtc,
                DateTimeOffset.UtcNow,
                gpuCompletionSeconds));
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning(
                "[Vulkan] Screenshot readback failure callback threw: {0}",
                ex.Message);
        }
    }

    private void FailPendingScreenshotReadbacksForDeviceLoss(string reason)
    {
        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            VulkanScreenshotReadbackSlot? slot = _screenshotReadbackSlots[i];
            if (slot is null ||
                Interlocked.CompareExchange(
                    ref slot.State,
                    (int)EVulkanScreenshotReadbackSlotState.Abandoned,
                    (int)EVulkanScreenshotReadbackSlotState.Submitted) !=
                (int)EVulkanScreenshotReadbackSlotState.Submitted)
            {
                continue;
            }

            DeliverScreenshotReadbackFailure(
                slot,
                $"Vulkan device loss aborted screenshot readback slot {i}: {reason}",
                slot.SubmittedTimestamp == 0
                    ? null
                    : Stopwatch.GetElapsedTime(slot.SubmittedTimestamp).TotalSeconds);
            ReleaseScreenshotReadbackReservation(slot);
        }
    }

    private bool RejectScreenshotReadback(string error, out string? failure)
    {
        failure = error;
        Interlocked.Increment(ref _screenshotReadbackRejectedCount);
        return false;
    }

    private bool RejectPreparedScreenshotReadback(
        VulkanScreenshotReadbackSlot slot,
        string error,
        out string? failure)
    {
        failure = error;
        Interlocked.Increment(ref _screenshotReadbackRejectedCount);
        return false;
    }

    private void RecycleUnsubmittedScreenshotReadback(VulkanScreenshotReadbackSlot slot)
    {
        ReleaseScreenshotReadbackReservation(slot);
        slot.Callback = null;

        if (_deviceLost)
        {
            Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.Abandoned);
            return;
        }

        if (slot.CommandBuffer.Handle != 0)
            ResetVulkanCommandBufferTracked(slot.CommandBuffer);
        ReleaseScreenshotReadbackStaging(slot);
        ClearScreenshotReadbackRequest(slot);
        Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.Idle);
    }

    private bool TryReserveScreenshotReadbackBytes(ulong rawByteCount)
    {
        long requested = checked((long)rawByteCount);
        while (true)
        {
            long current = Interlocked.Read(ref _screenshotReadbackReservedRawBytes);
            if (current < 0 || (ulong)current + rawByteCount > MaximumScreenshotReadbackRawBytes)
                return false;

            if (Interlocked.CompareExchange(
                    ref _screenshotReadbackReservedRawBytes,
                    checked(current + requested),
                    current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseScreenshotReadbackReservation(VulkanScreenshotReadbackSlot slot)
    {
        if (Interlocked.Exchange(ref slot.ReservationReleased, 1) != 0)
            return;

        ReleaseScreenshotReadbackReservation(slot.RawByteCount);
    }

    private void ReleaseScreenshotReadbackReservation(ulong rawByteCount)
    {
        if (rawByteCount == 0)
            return;

        long remaining = Interlocked.Add(
            ref _screenshotReadbackReservedRawBytes,
            -checked((long)rawByteCount));
        if (remaining < 0)
            Interlocked.Exchange(ref _screenshotReadbackReservedRawBytes, 0);
    }

    private void ReleaseScreenshotReadbackStaging(VulkanScreenshotReadbackSlot slot)
    {
        if (slot.StagingBuffer.Handle == 0)
            return;

        DestroyBuffer(slot.StagingBuffer, slot.StagingMemory);
        slot.StagingBuffer = default;
        slot.StagingMemory = default;
    }

    private void ClearScreenshotReadbackRequest(VulkanScreenshotReadbackSlot slot)
    {
        slot.Callback = null;
        slot.RawByteCount = 0;
        slot.Width = 0;
        slot.Height = 0;
        slot.WithTransparency = false;
        slot.UsedMultisampleResolve = false;
        slot.SubmittedTimestamp = 0;
        slot.FenceSignaledTimestamp = 0;
        slot.SubmittedAtUtc = default;
    }

    private void EvictIdleScreenshotResolveImages(
        VulkanScreenshotReadbackSlot requestingSlot,
        ulong requiredBytes)
    {
        ulong retainedBytes = GetRetainedScreenshotResolveImageBytes();
        if (retainedBytes + requiredBytes <= MaximumScreenshotResolveImageBytes)
            return;

        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            VulkanScreenshotReadbackSlot? candidate = _screenshotReadbackSlots[i];
            if (candidate is null ||
                ReferenceEquals(candidate, requestingSlot) ||
                candidate.ResolveImage.Handle == 0 ||
                Volatile.Read(ref candidate.State) != (int)EVulkanScreenshotReadbackSlotState.Idle)
            {
                continue;
            }

            DestroyScreenshotResolveImage(candidate, "ScreenshotReadback.ResolveBudgetEviction");
            retainedBytes = GetRetainedScreenshotResolveImageBytes();
            if (retainedBytes + requiredBytes <= MaximumScreenshotResolveImageBytes)
                return;
        }
    }

    private ulong GetRetainedScreenshotResolveImageBytes()
    {
        ulong total = 0;
        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
            total += _screenshotReadbackSlots[i]?.ResolveByteCount ?? 0;
        return total;
    }

    private void DestroyScreenshotResolveImage(
        VulkanScreenshotReadbackSlot slot,
        string owner)
    {
        Image image = slot.ResolveImage;
        if (image.Handle == 0)
            return;

        VulkanMemoryAllocation allocation = slot.ResolveAllocation;
        ClearTrackedImageLayouts(image);
        if (_imageAllocations.TryRemove(image.Handle, out VulkanMemoryAllocation trackedAllocation))
            allocation = trackedAllocation;
        UntrackImageAllocation(image);
        DestroyVulkanImageImmediateTracked(image, owner);
        if (!allocation.IsNull)
            FreeMemoryAllocation(allocation);

        slot.ResolveImage = default;
        slot.ResolveAllocation = default;
        slot.ResolveFormat = default;
        slot.ResolveWidth = 0;
        slot.ResolveHeight = 0;
        slot.ResolveByteCount = 0;
    }

    private void DrainScreenshotReadbacksForShutdown()
    {
        if (_deviceLost)
        {
            FailPendingScreenshotReadbacksForDeviceLoss(DeviceLostReason ?? "Vulkan renderer shutdown after device loss.");
            return;
        }

        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            VulkanScreenshotReadbackSlot? slot = _screenshotReadbackSlots[i];
            if (slot is not null &&
                Volatile.Read(ref slot.State) == (int)EVulkanScreenshotReadbackSlotState.Submitted)
            {
                TryConsumeScreenshotReadback(slot, i);
            }
        }
    }

    private void DisposeScreenshotReadbacks()
    {
        for (int i = 0; i < _screenshotReadbackSlots.Length; ++i)
        {
            VulkanScreenshotReadbackSlot? slot = _screenshotReadbackSlots[i];
            if (slot is null)
                continue;

            EVulkanScreenshotReadbackSlotState state =
                (EVulkanScreenshotReadbackSlotState)Volatile.Read(ref slot.State);
            if (state is EVulkanScreenshotReadbackSlotState.Submitted or
                EVulkanScreenshotReadbackSlotState.Preparing)
            {
                DeliverScreenshotReadbackFailure(
                    slot,
                    "The Vulkan renderer shut down before screenshot readback completion.",
                    gpuCompletionSeconds: null);
            }

            ReleaseScreenshotReadbackReservation(slot);
            ReleaseScreenshotReadbackStaging(slot);

            if (slot.ResolveImage.Handle != 0)
                DestroyScreenshotResolveImage(slot, "ScreenshotReadback.Dispose");
            if (slot.Fence.Handle != 0)
                Api!.DestroyFence(device, slot.Fence, null);
            if (slot.CommandBuffer.Handle != 0)
            {
                CommandBuffer commandBuffer = slot.CommandBuffer;
                FreeVulkanCommandBufferTracked(
                    slot.CommandPool,
                    ref commandBuffer,
                    "ScreenshotReadback.Dispose");
                RemoveCommandBufferBindState(slot.CommandBuffer);
            }

            slot.Callback = null;
            slot.Fence = default;
            slot.CommandBuffer = default;
            Volatile.Write(ref slot.State, (int)EVulkanScreenshotReadbackSlotState.Disposed);
        }

        Interlocked.Exchange(ref _screenshotReadbackReservedRawBytes, 0);
    }
}
