using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns native recording, submission, synchronization, and settlement for
/// renderer-facing readback operations whose source handles are already frozen.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal unsafe bool TryReadBufferBytes(
        Buffer source,
        ulong sourceByteOffset,
        Span<byte> destination,
        out string reason)
    {
        reason = "<missing>";
        if (source.Handle == 0)
            return false;

        if (destination.IsEmpty)
        {
            reason = "<empty>";
            return true;
        }

        ulong byteCount = checked((ulong)destination.Length);
        var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(byteCount);
        try
        {
            using (var scope = NewCommandScope())
            {
                BufferMemoryBarrier sourceBarrier = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask =
                        AccessFlags.ShaderWriteBit |
                        AccessFlags.TransferWriteBit |
                        AccessFlags.MemoryWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = source,
                    Offset = sourceByteOffset,
                    Size = byteCount,
                };

                CmdPipelineBarrierTracked(
                    scope.CommandBuffer,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    1,
                    &sourceBarrier,
                    0,
                    null);

                BufferCopy copy = new()
                {
                    SrcOffset = sourceByteOffset,
                    DstOffset = 0,
                    Size = byteCount,
                };
                CmdCopyBufferTracked(scope.CommandBuffer, source, stagingBuffer, 1, ref copy);
            }

            if (!ResourceRuntime.Buffers.TryCreateMappedSlice(
                    ReadbackContext, stagingBuffer, stagingMemory, 0, byteCount, out VulkanMappedMemorySlice mappedSlice) ||
                !ResourceRuntime.Buffers.TryAcquireRead(ReadbackContext, in mappedSlice, out VulkanMappedMemoryReadLease readLease))
            {
                reason = "<map-failed>";
                return false;
            }

            using (readLease)
            {
                readLease.Bytes[..destination.Length].CopyTo(destination);
                reason = "gpu";
                return true;
            }
        }
        finally
        {
            DestroyReadbackBuffer(stagingBuffer, stagingMemory);
        }
    }

    internal unsafe void BeginDepthReadbackAsync(
        in BlitImageInfo source,
        int x,
        int y,
        Action<float> callback,
        VulkanReadbackOutputResourceService outputResources,
        int frameSlot)
    {
        if (!source.IsValid ||
            (source.AspectMask & ImageAspectFlags.DepthBit) == 0 ||
            !IsPixelInsideExtent(x, y, source.Extent) ||
            !DeviceContext.IsOperational)
        {
            callback?.Invoke(1.0f);
            return;
        }

        uint pixelSize = GetDepthFormatPixelSize(source.Format);
        if (pixelSize == 0)
        {
            callback?.Invoke(1.0f);
            return;
        }

        CommandPool commandPool = Pools.PrimaryGraphics;
        if (commandPool.Handle == 0)
        {
            callback?.Invoke(1.0f);
            return;
        }

        ulong bufferSize = pixelSize;
        if (!outputResources.TryAcquireDepthStagingSlice(
                frameSlot,
                bufferSize,
                out VulkanFrameDataSlice stagingSlice,
                out _))
        {
            callback?.Invoke(1.0f);
            return;
        }
        CommandBuffer commandBuffer = default;
        Fence fence = default;

        try
        {
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = commandPool,
                CommandBufferCount = 1,
            };
            if (AllocateCommandBufferWithLifetime(
                    ref allocateInfo,
                    out commandBuffer,
                    "Readback.Depth") != Result.Success)
            {
                CompleteFailedDepthReadback(
                    callback,
                    outputResources,
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    ref fence,
                    stagingSlice,
                    "Readback.AllocateFailure");
                return;
            }

            if (outputResources.CreateFence(
                    (FenceCreateFlags)0,
                    "Readback.Depth",
                    out fence) != Result.Success)
            {
                CompleteFailedDepthReadback(
                    callback,
                    outputResources,
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    ref fence,
                    stagingSlice,
                    "Readback.FenceFailure");
                return;
            }

            BeginRecording(
                Api,
                DeviceContext.StateMachine,
                commandBuffer,
                "vkBeginCommandBuffer.Readback.Depth",
                CommandBufferUsageFlags.OneTimeSubmitBit);
            ResetCommandBufferBindState(commandBuffer);

            RecordDepthPixelCopy(
                commandBuffer,
                source,
                x,
                y,
                stagingSlice.Buffer,
                stagingSlice.Offset);

            Result endResult = EndCommandBufferTracked(commandBuffer);
            DeviceContext.ObserveNativeResult("vkEndCommandBuffer.Readback.Depth", endResult);
            if (endResult != Result.Success)
            {
                CompleteFailedDepthReadback(
                    callback,
                    outputResources,
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    ref fence,
                    stagingSlice,
                    "Readback.EndFailure");
                return;
            }

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            if (!outputResources.TryPrepareStagingSlice(stagingSlice))
            {
                CompleteFailedDepthReadback(callback, outputResources, frameSlot, commandPool, ref commandBuffer, ref fence, stagingSlice, "Readback.PrepareFailure");
                return;
            }
            VulkanSubmissionDiagnosticContext diagnosticContext = new()
            {
                SubmissionKind = "DepthReadback",
                FrameSlot = frameSlot,
                CommandBufferCount = 1,
                FirstCommandBufferHandle = unchecked((ulong)commandBuffer.Handle),
                FenceHandle = unchecked((ulong)fence.Handle),
                QueueKind = "Graphics",
            };
            VulkanSubmissionReceipt submitReceipt = SubmitToQueueTrackedWithDisposition(
                DeviceContext.GraphicsQueue,
                ref submitInfo,
                fence,
                in diagnosticContext,
                out _,
                out _,
                "DepthReadback");
            if (!submitReceipt.SubmissionAccepted)
            {
                CompleteFailedDepthReadback(
                    callback,
                    outputResources,
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    ref fence,
                    stagingSlice,
                    "Readback.SubmitFailure");
                return;
            }
            outputResources.MarkStagingSliceSubmitted(stagingSlice);
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan.Readback] Failed to start asynchronous depth readback: {0}: {1}",
                exception.GetType().Name,
                exception.Message);
            CompleteFailedDepthReadback(
                callback,
                outputResources,
                frameSlot,
                commandPool,
                ref commandBuffer,
                ref fence,
                stagingSlice,
                "Readback.RecordFailure");
            return;
        }

        Format submittedFormat = source.Format;
        CommandBuffer submittedCommandBuffer = commandBuffer;
        Fence submittedFence = fence;
        Task settlementTask;
        try
        {
            settlementTask = Task.Run(() => SettleDepthReadback(
                submittedFormat,
                callback,
                outputResources,
                frameSlot,
                commandPool,
                submittedCommandBuffer,
                submittedFence,
                stagingSlice));
        }
        catch (Exception exception)
        {
            // Native acceptance already transferred ownership. Settle synchronously rather than
            // orphaning the fence, command buffer, or submitted arena slice on scheduling failure.
            Debug.VulkanWarning("[Vulkan.Readback] Async depth settlement scheduling failed; settling inline: {0}: {1}", exception.GetType().Name, exception.Message);
            SettleDepthReadback(submittedFormat, callback, outputResources, frameSlot, commandPool, submittedCommandBuffer, submittedFence, stagingSlice);
            return;
        }

        try
        {
            CommandBuffers.ReadbackTasks.Register(settlementTask);
        }
        catch (Exception exception)
        {
            // The task already owns settlement. Registration is observability only and must not
            // start a second consumer. Join it here so teardown cannot overtake untracked work.
            Debug.VulkanWarning(
                "[Vulkan.Readback] Async depth settlement task registration failed: {0}: {1}",
                exception.GetType().Name,
                exception.Message);
            settlementTask.GetAwaiter().GetResult();
        }
    }

    private unsafe void RecordDepthPixelCopy(
        CommandBuffer commandBuffer,
        in BlitImageInfo source,
        int x,
        int y,
        Buffer stagingBuffer,
        ulong stagingBufferOffset)
    {
        ImageMemoryBarrier toTransferBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = source.PreferredLayout,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = source.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = source.MipLevel,
                LevelCount = 1,
                BaseArrayLayer = source.BaseArrayLayer,
                LayerCount = 1,
            },
            SrcAccessMask = source.AccessMask,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        CmdPipelineBarrierTracked(
            commandBuffer,
            source.StageMask,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransferBarrier);

        BufferImageCopy copy = new()
        {
            BufferOffset = stagingBufferOffset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.DepthBit,
                MipLevel = source.MipLevel,
                BaseArrayLayer = source.BaseArrayLayer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D(1, 1, 1),
        };
        CopyImageToBufferTracked(
            commandBuffer,
            source.Image,
            ImageLayout.TransferSrcOptimal,
            stagingBuffer,
            1,
            ref copy);

        ImageMemoryBarrier restoreBarrier = toTransferBarrier with
        {
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = source.PreferredLayout,
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = source.AccessMask,
        };
        CmdPipelineBarrierTracked(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            source.StageMask,
            0,
            0,
            null,
            0,
            null,
            1,
            &restoreBarrier);
    }

    private void SettleDepthReadback(
        Format format,
        Action<float> callback,
        VulkanReadbackOutputResourceService outputResources,
        int frameSlot,
        CommandPool commandPool,
        CommandBuffer commandBuffer,
        Fence fence,
        VulkanFrameDataSlice stagingSlice)
    {
        bool ownershipSettled = false;
        try
        {
            if (!DeviceContext.IsOperational)
            {
                callback?.Invoke(1.0f);
                return;
            }

            Result waitResult;
            do
            {
                const ulong timeoutNanoseconds = 5_000_000_000;
                waitResult = Api.WaitForFences(
                    DeviceContext.Device,
                    1,
                    ref fence,
                    true,
                    timeoutNanoseconds);
                DeviceContext.ObserveNativeResult("vkWaitForFences.Readback.Depth", waitResult);
                if (waitResult == Result.Timeout)
                    Debug.VulkanWarning("[Vulkan.ResourceLifetime] Depth readback fence timed out; retaining its arena slice and retrying settlement.");
            }
            while (waitResult == Result.Timeout && DeviceContext.IsOperational);

            if (waitResult != Result.Success)
            {
                callback?.Invoke(1.0f);
                return;
            }

            CompleteTrackedFence(fence);
            if (!outputResources.TryCompleteStagingSlice(stagingSlice))
            {
                callback?.Invoke(1.0f);
                return;
            }
            ownershipSettled = true;
            if (!outputResources.TryBeginRead(stagingSlice, out VulkanFrameDataReadScope readScope))
            {
                callback?.Invoke(1.0f);
                return;
            }

            try
            {
                callback?.Invoke(ReadDepthValue(readScope.Bytes, format));
            }
            finally
            {
                readScope.Dispose();
            }
        }
        finally
        {
            if (ownershipSettled)
            {
                outputResources.DestroyFence(fence);
                FreeCommandBufferWithLifetime(
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    "Readback.AsyncComplete");
            }
            else
            {
                RetireIncompleteSynchronousSubmission(
                    commandBuffer,
                    commandPool,
                    fence,
                    ResourceRuntime.ReadbackFrameDataArena,
                    in stagingSlice,
                    removeOneTimeOwner: false,
                    "Readback.Depth",
                    frameSlotLifetime: frameSlot);
            }
        }
    }

    private void CompleteFailedDepthReadback(
        Action<float> callback,
        VulkanReadbackOutputResourceService outputResources,
        int frameSlot,
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        ref Fence fence,
        VulkanFrameDataSlice stagingSlice,
        string owner)
    {
        outputResources.DestroyFence(ref fence);
        FreeCommandBufferWithLifetime(
            frameSlot,
            commandPool,
            ref commandBuffer,
            owner);
        outputResources.CancelStagingSliceSubmission(stagingSlice);
        callback?.Invoke(1.0f);
    }
}
