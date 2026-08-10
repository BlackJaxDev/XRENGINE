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
internal sealed unsafe partial class VulkanCommandRuntime
{
    internal bool TryReadBufferBytes(
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
                CmdCopyBufferTracked(scope.CommandBuffer, source, stagingBuffer, 1, &copy);
            }

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, byteCount, out void* mappedPointer))
            {
                reason = "<map-failed>";
                return false;
            }

            try
            {
                new Span<byte>(mappedPointer, destination.Length).CopyTo(destination);
                reason = "gpu";
                return true;
            }
            finally
            {
                UnmapReadbackMemory(stagingBuffer, stagingMemory);
            }
        }
        finally
        {
            DestroyReadbackBuffer(stagingBuffer, stagingMemory);
        }
    }

    internal void BeginDepthReadbackAsync(
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
        var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);
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
                    stagingBuffer,
                    stagingMemory,
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
                    stagingBuffer,
                    stagingMemory,
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
                stagingBuffer);

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
                    stagingBuffer,
                    stagingMemory,
                    "Readback.EndFailure");
                return;
            }

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            Result submitResult = SubmitToQueueTracked(
                Api,
                DeviceContext,
                FrameTelemetry,
                DeviceContext.GraphicsQueue,
                ref submitInfo,
                fence,
                "vkQueueSubmit.Readback.Depth");
            if (submitResult != Result.Success)
            {
                CompleteFailedDepthReadback(
                    callback,
                    outputResources,
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    ref fence,
                    stagingBuffer,
                    stagingMemory,
                    "Readback.SubmitFailure");
                return;
            }
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
                stagingBuffer,
                stagingMemory,
                "Readback.RecordFailure");
            return;
        }

        Format submittedFormat = source.Format;
        CommandBuffer submittedCommandBuffer = commandBuffer;
        Fence submittedFence = fence;
        Task settlementTask = Task.Run(() => SettleDepthReadback(
            submittedFormat,
            callback,
            outputResources,
            frameSlot,
            commandPool,
            submittedCommandBuffer,
            submittedFence,
            stagingBuffer,
            stagingMemory,
            bufferSize));
        CommandBuffers.ReadbackTasks.Register(settlementTask);
    }

    private void RecordDepthPixelCopy(
        CommandBuffer commandBuffer,
        in BlitImageInfo source,
        int x,
        int y,
        Buffer stagingBuffer)
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
            BufferOffset = 0,
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
            &copy);

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
        Buffer stagingBuffer,
        DeviceMemory stagingMemory,
        ulong bufferSize)
    {
        bool submissionCompleted = false;
        try
        {
            if (!DeviceContext.IsOperational)
            {
                callback?.Invoke(1.0f);
                return;
            }

            const ulong timeoutNanoseconds = 5_000_000_000;
            Result waitResult = Api.WaitForFences(
                DeviceContext.Device,
                1,
                ref fence,
                true,
                timeoutNanoseconds);
            DeviceContext.ObserveNativeResult("vkWaitForFences.Readback.Depth", waitResult);
            if (waitResult != Result.Success)
            {
                callback?.Invoke(1.0f);
                return;
            }

            CompleteTrackedFence(fence);
            submissionCompleted = true;
            if (!TryMapReadbackMemory(
                    stagingBuffer,
                    stagingMemory,
                    0,
                    bufferSize,
                    out void* mappedPointer))
            {
                callback?.Invoke(1.0f);
                return;
            }

            try
            {
                callback?.Invoke(ReadDepthValue(mappedPointer, format));
            }
            finally
            {
                UnmapReadbackMemory(stagingBuffer, stagingMemory);
            }
        }
        finally
        {
            if (!submissionCompleted)
                Debug.VulkanWarning(
                    "[Vulkan.ResourceLifetime] Preserving timed-out depth-readback fence, command buffer, and staging buffer because GPU completion was not proven.");
            else
            {
                outputResources.DestroyFence(fence);
                FreeCommandBufferWithLifetime(
                    frameSlot,
                    commandPool,
                    ref commandBuffer,
                    "Readback.AsyncComplete");
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
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
        Buffer stagingBuffer,
        DeviceMemory stagingMemory,
        string owner)
    {
        outputResources.DestroyFence(ref fence);
        FreeCommandBufferWithLifetime(
            frameSlot,
            commandPool,
            ref commandBuffer,
            owner);
        DestroyReadbackBuffer(stagingBuffer, stagingMemory);
        callback?.Invoke(1.0f);
    }
}
