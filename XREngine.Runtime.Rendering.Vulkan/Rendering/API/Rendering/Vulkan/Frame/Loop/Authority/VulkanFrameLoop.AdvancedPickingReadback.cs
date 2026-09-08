using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal unsafe bool TryQueueAdvancedPickingReadback(
        XRTexture identity,
        XRTexture metadata,
        XRTexture selection,
        in AdvancedPickingQuery query,
        Action<AdvancedVisibilityEncodedSurface> callback,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(callback);
        failure = null;
        if (_deviceLost || !_deviceContext.IsOperational)
        {
            failure = DeviceLostReason ?? "The Vulkan device is not operational.";
            return false;
        }
        if (!RuntimeEngine.IsRenderThread)
        {
            failure = "Vulkan Advanced picking readback must be queued on the render thread.";
            return false;
        }

        PollScreenshotReadbacks();
        if (!TryResolveAdvancedPickingSource(
                identity,
                query,
                Format.R32G32Uint,
                out BlitImageInfo identitySource) ||
            !TryResolveAdvancedPickingSource(
                metadata,
                query,
                Format.R32Uint,
                out BlitImageInfo metadataSource) ||
            !TryResolveAdvancedPickingSource(
                selection,
                query,
                Format.R32Uint,
                out BlitImageInfo selectionSource))
        {
            failure = "Vulkan could not resolve the complete transfer-readable Advanced visibility texture set for the requested pixel.";
            return false;
        }

        if (!TryReserveScreenshotReadbackBytes(AdvancedPickingContract.ReadbackByteCount))
        {
            failure = "The bounded Vulkan asynchronous readback budget is exhausted.";
            return false;
        }

        bool reservationOwned = true;
        VulkanScreenshotReadbackSlot? slot = AcquireScreenshotReadbackSlot(
            Format.R32G32Uint,
            2u,
            1u,
            needsResolve: false,
            out int slotIndex);
        if (slot is null)
        {
            ReleaseScreenshotReadbackReservation(AdvancedPickingContract.ReadbackByteCount);
            failure = "All bounded Vulkan asynchronous readback slots are busy.";
            return false;
        }

        bool stagingPrepared = false;
        bool submitted = false;
        try
        {
            PrepareAdvancedPickingReadbackRequest(
                slot,
                identity,
                metadata,
                selection,
                callback);
            reservationOwned = false;
            if (!EnsureScreenshotReadbackResources(
                    slot,
                    slotIndex,
                    Format.R32G32Uint,
                    2u,
                    1u,
                    8u,
                    needsResolve: false,
                    out failure))
            {
                return false;
            }

            Result resetFenceResult = Api!.ResetFences(
                _deviceContext.Device,
                1,
                in slot.Fence);
            Result resetCommandResult =
                _commandRuntime.ResetTrackedCommandBuffer(slot.CommandBuffer);
            if (resetFenceResult != Result.Success ||
                resetCommandResult != Result.Success)
            {
                failure = $"Failed to reset Vulkan Advanced picking resources (fence={resetFenceResult}, command={resetCommandResult}).";
                return false;
            }

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _deviceContext.ThrowIfVulkanDeviceOperationNotAdmitted(
                "vkBeginCommandBuffer.AdvancedPickingReadback");
            Result beginResult = _commandRuntime.BeginTrackedCommandBuffer(
                slot.CommandBuffer,
                ref beginInfo,
                "AdvancedPickingReadback");
            if (beginResult != Result.Success)
            {
                failure = $"vkBeginCommandBuffer failed for Advanced picking ({beginResult}).";
                return false;
            }

            RecordAdvancedPickingReadback(
                slot,
                identitySource,
                metadataSource,
                selectionSource,
                query);
            Result endResult =
                _commandRuntime.EndCommandBufferTracked(slot.CommandBuffer);
            if (endResult != Result.Success)
            {
                failure = $"vkEndCommandBuffer failed for Advanced picking ({endResult}).";
                return false;
            }

            if (!ReadbackOutputResources.TryPrepareStagingSlice(slot.StagingSlice))
            {
                failure = "The Vulkan readback arena could not publish the Advanced picking staging slice.";
                return false;
            }
            stagingPrepared = true;

            CommandBuffer commandBuffer = slot.CommandBuffer;
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            VulkanSubmissionDiagnosticContext diagnosticContext = new()
            {
                SubmissionKind = "AdvancedPickingReadback",
                FrameSlot = slot.StagingSlice.FrameSlot,
                CommandBufferCount = 1,
                FirstCommandBufferHandle = unchecked((ulong)commandBuffer.Handle),
                FenceHandle = unchecked((ulong)slot.Fence.Handle),
                QueueKind = "Graphics",
            };
            VulkanSubmissionReceipt receipt =
                _commandRuntime.SubmitToQueueTrackedWithDisposition(
                    _deviceContext.GraphicsQueue,
                    ref submitInfo,
                    slot.Fence,
                    in diagnosticContext,
                    out _,
                    out _,
                    "VulkanAdvancedPickingReadback");
            if (!receipt.SubmissionAccepted)
            {
                failure = $"Vulkan Advanced picking submission failed ({receipt.Result}).";
                return false;
            }

            ReadbackOutputResources.MarkStagingSliceSubmitted(slot.StagingSlice);
            Volatile.Write(
                ref slot.State,
                (int)EVulkanScreenshotReadbackSlotState.Submitted);
            submitted = true;
            slot.SubmittedTimestamp = Stopwatch.GetTimestamp();
            slot.SubmittedAtUtc = DateTimeOffset.UtcNow;
            VulkanReadbackLayoutPolicy.PublishRestoredAttachmentLayout(
                identitySource,
                VulkanReadbackLayoutPolicy.ResolvePostTransfer(identitySource));
            VulkanReadbackLayoutPolicy.PublishRestoredAttachmentLayout(
                metadataSource,
                VulkanReadbackLayoutPolicy.ResolvePostTransfer(metadataSource));
            VulkanReadbackLayoutPolicy.PublishRestoredAttachmentLayout(
                selectionSource,
                VulkanReadbackLayoutPolicy.ResolvePostTransfer(selectionSource));
            return true;
        }
        catch (Exception ex)
        {
            failure = $"Failed to queue Vulkan Advanced picking readback: {ex.Message}";
            return false;
        }
        finally
        {
            if (reservationOwned)
                ReleaseScreenshotReadbackReservation(
                    AdvancedPickingContract.ReadbackByteCount);
            if (stagingPrepared && !submitted)
                ReadbackOutputResources.CancelStagingSliceSubmission(slot.StagingSlice);
            if (!submitted)
                RecycleUnsubmittedScreenshotReadback(slot);
        }
    }

    private bool TryResolveAdvancedPickingSource(
        XRTexture texture,
        in AdvancedPickingQuery query,
        Format expectedFormat,
        out BlitImageInfo source)
    {
        if (!TryResolveTextureBlitImage(
                texture,
                0,
                checked((int)query.ViewIndex),
                ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.FragmentShaderBit |
                PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.MemoryReadBit,
                out BlitImageInfo candidate) ||
            !TryResolveLiveBlitImage(candidate, out BlitImageInfo liveSource) ||
            liveSource.Format != expectedFormat ||
            liveSource.Samples != SampleCountFlags.Count1Bit ||
            query.CoordX >= liveSource.Extent.Width ||
            query.CoordY >= liveSource.Extent.Height)
        {
            source = default;
            return false;
        }

        source = liveSource;
        return true;
    }

    private static void PrepareAdvancedPickingReadbackRequest(
        VulkanScreenshotReadbackSlot slot,
        XRTexture identity,
        XRTexture metadata,
        XRTexture selection,
        Action<AdvancedVisibilityEncodedSurface> callback)
    {
        slot.Callback = null;
        slot.AdvancedPickingCallback = callback;
        slot.IsAdvancedPicking = true;
        slot.AdvancedPickingIdentitySource = identity;
        slot.AdvancedPickingMetadataSource = metadata;
        slot.AdvancedPickingSelectionSource = selection;
        slot.SourceFormat = Format.R32G32Uint;
        slot.Width = 2;
        slot.Height = 1;
        slot.RawByteCount = AdvancedPickingContract.ReadbackByteCount;
        slot.WithTransparency = false;
        slot.UsedMultisampleResolve = false;
        slot.SubmittedTimestamp = 0;
        slot.FenceSignaledTimestamp = 0;
        slot.SubmittedAtUtc = default;
        Volatile.Write(ref slot.CallbackDelivered, 0);
        Volatile.Write(ref slot.ReservationReleased, 0);
        Volatile.Write(ref slot.WatchdogWarningLogged, 0);
    }

    private unsafe void RecordAdvancedPickingReadback(
        VulkanScreenshotReadbackSlot slot,
        in BlitImageInfo identity,
        in BlitImageInfo metadata,
        in BlitImageInfo selection,
        in AdvancedPickingQuery query)
    {
        RecordAdvancedPickingSourceCopy(slot, identity, query, 0u);
        RecordAdvancedPickingSourceCopy(slot, metadata, query, 2u * sizeof(uint));
        RecordAdvancedPickingSourceCopy(slot, selection, query, 3u * sizeof(uint));
    }

    private unsafe void RecordAdvancedPickingSourceCopy(
        VulkanScreenshotReadbackSlot slot,
        in BlitImageInfo source,
        in AdvancedPickingQuery query,
        ulong byteOffset)
    {
        ImageLayout restoreLayout =
            VulkanReadbackLayoutPolicy.ResolvePostTransfer(source);
        TransitionForBlit(
            slot.CommandBuffer,
            source,
            source.PreferredLayout,
            ImageLayout.TransferSrcOptimal,
            source.AccessMask,
            AccessFlags.TransferReadBit,
            source.StageMask,
            PipelineStageFlags.TransferBit);

        BufferImageCopy copy = new()
        {
            BufferOffset = slot.StagingSlice.Offset + byteOffset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = source.MipLevel,
                BaseArrayLayer = source.BaseArrayLayer,
                LayerCount = 1u,
            },
            ImageOffset = new Offset3D
            {
                X = checked((int)query.CoordX),
                Y = checked((int)query.CoordY),
                Z = 0,
            },
            ImageExtent = new Extent3D
            {
                Width = 1u,
                Height = 1u,
                Depth = 1u,
            },
        };
        _commandRuntime.CopyImageToBufferTracked(
            slot.CommandBuffer,
            source.Image,
            ImageLayout.TransferSrcOptimal,
            slot.StagingSlice.Buffer,
            1u,
            ref copy);
        TransitionForBlit(
            slot.CommandBuffer,
            source,
            ImageLayout.TransferSrcOptimal,
            restoreLayout,
            AccessFlags.TransferReadBit,
            source.AccessMask,
            PipelineStageFlags.TransferBit,
            source.StageMask);
    }

    private void ProcessAdvancedPickingReadback(
        VulkanScreenshotReadbackSlot slot,
        VulkanPooledReadbackBytes rawBytes)
    {
        try
        {
            AdvancedVisibilityEncodedSurface encoded =
                AdvancedVisibilityEncodedSurface.Invalid;
            ReadOnlySpan<byte> bytes = rawBytes.Bytes;
            if (bytes.Length >= AdvancedPickingContract.ReadbackByteCount)
            {
                uint draw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                uint primitive = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
                uint metadata = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
                uint selection = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
                encoded = new AdvancedVisibilityEncodedSurface(
                    new AdvancedVisibilityPayloadWords(draw, primitive),
                    new AdvancedVisibilityMetadataWord(metadata),
                    selection);
            }

            if (Interlocked.Exchange(ref slot.CallbackDelivered, 1) != 0)
                return;

            Action<AdvancedVisibilityEncodedSurface>? callback =
                Interlocked.Exchange(ref slot.AdvancedPickingCallback, null);
            callback?.Invoke(encoded);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning(
                "[Vulkan] Failed to deliver Advanced picking readback: {0}",
                ex.Message);
        }
        finally
        {
            rawBytes.Dispose();
            ReleaseScreenshotReadbackReservation(slot);
            ClearScreenshotReadbackRequest(slot);
            if (Volatile.Read(ref slot.State) !=
                (int)EVulkanScreenshotReadbackSlotState.Disposed)
            {
                Volatile.Write(
                    ref slot.State,
                    _deviceLost
                        ? (int)EVulkanScreenshotReadbackSlotState.Abandoned
                        : (int)EVulkanScreenshotReadbackSlotState.Idle);
            }
        }
    }
}
