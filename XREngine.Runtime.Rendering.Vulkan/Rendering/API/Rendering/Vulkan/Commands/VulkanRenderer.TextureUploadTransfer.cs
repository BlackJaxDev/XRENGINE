using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    internal bool HasDedicatedTextureUploadTransferQueue
    {
        get
        {
            QueueFamilyIndices families = _deviceContext.QueueFamilies;
            return _deviceContext.TransferQueue.Handle != 0
                && families.GraphicsFamilyIndex.HasValue
                && families.TransferFamilyIndex.HasValue
                && families.TransferFamilyIndex.Value != families.GraphicsFamilyIndex.Value;
        }
    }

    /// <summary>
    /// Submits an imported texture copy on the graphics queue. A dedicated
    /// transfer queue requires an explicit semaphore release/acquire chain;
    /// until that chain exists it is not a valid immediate-readiness path.
    /// </summary>
    internal unsafe bool TrySubmitImportedTextureUploadBatchToGraphicsQueue(
        IReadOnlyList<VulkanImportedTexturePendingUpload> uploads,
        out VulkanSubmittedImportedTextureUploadBatch? submitted,
        out string? failureReason)
    {
        submitted = null;
        failureReason = null;

        if (_deviceLost || uploads.Count == 0)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        QueueFamilyIndices families = _deviceContext.QueueFamilies;
        uint graphicsFamily = families.GraphicsFamilyIndex ?? 0u;

        for (int index = 0; index < uploads.Count; index++)
            if (!uploads[index].TryValidateTransferOwnership(ResourceRuntime, out string? validationFailure))
            {
                failureReason = validationFailure;
                return false;
            }

        Queue submissionQueue = _deviceContext.GraphicsQueue;
        if (submissionQueue.Handle == 0)
        {
            failureReason = "no Vulkan queue is available for foreground upload submission";
            return false;
        }
        CommandPool pool = GetThreadCommandPool();
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        VulkanTextureUploadGpuTimestampLease gpuTimestampLease = default;
        bool gpuTimestampLeaseTransferred = false;
        try
        {
            _ = ResourceRuntime.Uploads.TryAcquireTransferGpuTimestampLease(
                out gpuTimestampLease);

            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = pool,
                CommandBufferCount = 1,
            };

            Result allocateResult = AllocateVulkanCommandBufferTracked(ref allocateInfo, out commandBuffer, "TextureUpload.Transfer");
            if (allocateResult != Result.Success || commandBuffer.Handle == 0)
            {
                failureReason = $"failed to allocate transfer command buffer ({allocateResult})";
                return false;
            }

            try
            {
                _commandRuntime.BeginRecording(
                    Api!,
                    _deviceContext.StateMachine,
                    commandBuffer,
                    "vkBeginCommandBuffer.TextureUploadTransfer",
                    CommandBufferUsageFlags.OneTimeSubmitBit);
            }
            catch (InvalidOperationException ex)
            {
                failureReason = ex.Message;
                return false;
            }

            if (gpuTimestampLease.IsValid)
            {
                TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    gpuTimestampLease.QueryPool.Handle,
                    "TextureUpload.TransferGpuTiming");
                Api!.CmdResetQueryPool(commandBuffer, gpuTimestampLease.QueryPool, 0, 2);
                Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit,
                    gpuTimestampLease.QueryPool, 0);
            }

            for (int index = 0; index < uploads.Count; index++)
                RecordImportedTextureTransferUpload(commandBuffer, uploads[index]);

            if (gpuTimestampLease.IsValid)
                Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit,
                    gpuTimestampLease.QueryPool, 1);

            Result endResult = EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                failureReason = $"failed to end transfer command buffer ({endResult})";
                return false;
            }

            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = 0,
            };
            Result fenceResult = Api!.CreateFence(_deviceContext.Device, ref fenceCreateInfo, null, out fence);
            if (fenceResult != Result.Success || fence.Handle == 0)
            {
                failureReason = $"failed to create transfer upload fence ({fenceResult})";
                return false;
            }

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            // Build the managed owner before the native acceptance boundary. After queue
            // acceptance, transferring these handles into the record is non-allocating.
            VulkanImportedTexturePendingUpload[] acceptedUploads = new VulkanImportedTexturePendingUpload[uploads.Count];
            long bytesInFlight = 0;
            for (int index = 0; index < uploads.Count; index++)
            {
                VulkanImportedTexturePendingUpload upload = uploads[index];
                acceptedUploads[index] = upload;
                bytesInFlight = checked(bytesInFlight + CalculateUploadStagingBytes(upload));
            }
            VulkanSubmittedImportedTextureUploadBatch acceptedOwner = new(
                acceptedUploads,
                commandBuffer,
                pool,
                fence,
                TextureRuntimeDiagnostics.StartTiming(),
                bytesInFlight,
                gpuTimestampLease);

            VulkanSubmissionReceipt submitReceipt;
            submitReceipt = SubmitToQueueTrackedWithDisposition(
                submissionQueue,
                ref submitInfo,
                fence,
                new VulkanSubmissionDiagnosticContext
                {
                    SubmissionKind = "TextureUpload.TransferBatch",
                    QueueKind = "GraphicsTextureUpload",
                    CommandBufferCount = 1,
                    FirstCommandBufferHandle = (ulong)commandBuffer.Handle,
                    FenceHandle = fence.Handle,
                },
                out _,
                out _,
                "TextureUpload.GraphicsForeground");

            if (!submitReceipt.SubmissionAccepted)
            {
                failureReason = $"transfer queue submit failed ({submitReceipt.Result})";
                return false;
            }

            // Native submission has accepted the command buffer and staging sources. Transfer
            // those handles to the submitted-work record before any later managed publication.
            submitted = acceptedOwner;
            gpuTimestampLeaseTransferred = gpuTimestampLease.IsValid;
            commandBuffer = default;
            fence = default;
            if (!submitReceipt.LifetimePinsTransferred ||
                !submitReceipt.PostSubmissionPublicationSucceeded)
            {
                _ = acceptedOwner.TryMarkTerminalFailure(
                    "Texture upload command submission was accepted, but its lifetime pin or post-submission publication did not complete.");
            }
            return true;
        }
        finally
        {
            if (fence.Handle != 0)
                Api!.DestroyFence(_deviceContext.Device, fence, null);

            if (commandBuffer.Handle != 0)
            {
                RemoveCommandBufferBindState(commandBuffer);
                FreeVulkanCommandBufferTracked(pool, ref commandBuffer, "TextureUpload.TransferFailure");
            }

            // A rejected recording/submit never exposes this pair past the
            // tracked command buffer released above.
            if (!gpuTimestampLeaseTransferred)
                ResourceRuntime.Uploads.ReleaseTransferGpuTimestampLease(gpuTimestampLease);
        }
    }

    internal bool TryPollImportedTextureTransfer(
        VulkanSubmittedImportedTextureUploadBatch submitted,
        out bool complete,
        out string? failureReason)
    {
        complete = false;
        failureReason = null;

        if (submitted.IsNativeCompletionFinished)
        {
            complete = true;
            return true;
        }
        if (submitted.IsNativeCompletionFaulted || submitted.IsNativeCompletionInProgress)
        {
            failureReason =
                "Imported texture transfer native completion is quarantined after a cleanup fault or concurrent completion attempt.";
            return false;
        }
        if (submitted.IsFenceCompletionProven)
        {
            complete = true;
            return true;
        }

        if (_deviceLost)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        Result result = Api!.GetFenceStatus(_deviceContext.Device, submitted.Fence);
        if (result == Result.Success)
        {
            CompleteTrackedFence(submitted.Fence);
            submitted.MarkFenceCompletionProven();
            complete = true;
            return true;
        }

        if (result == Result.NotReady || result == Result.Timeout)
            return true;

        DeviceContext.ObserveNativeResult(
            "vkGetFenceStatus.TextureUploadTransfer",
            result);

        failureReason = $"transfer upload fence status failed ({result})";
        return false;
    }


    internal unsafe bool CompleteSubmittedImportedTextureUploadBatch(
        VulkanSubmittedImportedTextureUploadBatch submitted,
        out string? failureReason)
    {
        failureReason = null;
        if (_deviceLost)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        if (submitted.IsNativeCompletionFinished)
            return true;
        if (!submitted.IsFenceCompletionProven)
        {
            failureReason =
                "Refusing to release an imported texture transfer without a successful fence completion proof.";
            return false;
        }
        if (!submitted.TryBeginNativeCompletion())
        {
            failureReason =
                "Imported texture transfer native completion is already in progress or quarantined.";
            return false;
        }

        try
        {
            TryReadSubmittedImportedTextureTransferGpuTiming(submitted);
            CommandBuffer commandBuffer = submitted.CommandBuffer;
            if (commandBuffer.Handle != 0)
                FreeVulkanCommandBufferTracked(submitted.CommandPool, ref commandBuffer, "TextureUpload.TransferComplete");
            RemoveCommandBufferBindState(submitted.CommandBuffer);
            if (submitted.Fence.Handle != 0)
                Api!.DestroyFence(_deviceContext.Device, submitted.Fence, null);
            ResourceRuntime.Uploads.ReleaseTransferGpuTimestampLease(submitted.GpuTimestampLease);
            submitted.MarkNativeCompletionFinished();
            return true;
        }
        catch (Exception exception)
        {
            submitted.MarkNativeCompletionFaulted();
            failureReason = $"Imported texture transfer native completion faulted: {exception.Message}";
            return false;
        }
    }

    private unsafe void TryReadSubmittedImportedTextureTransferGpuTiming(
        VulkanSubmittedImportedTextureUploadBatch submitted)
    {
        VulkanTextureUploadGpuTimestampLease lease = submitted.GpuTimestampLease;
        if (!lease.IsValid)
            return;

        ulong* timestamps = stackalloc ulong[2];
        Result result = Api!.GetQueryPoolResults(
            _deviceContext.Device,
            lease.QueryPool,
            0,
            2,
            (nuint)(sizeof(ulong) * 2),
            timestamps,
            (ulong)sizeof(ulong),
            QueryResultFlags.Result64Bit);
        if (result != Result.Success)
        {
            VulkanTextureUploadService.RecordImportedTextureTransferGpuUnavailable();
            return;
        }

        ResourceRuntime.NotifyResourceUseCompleted(ObjectType.QueryPool, lease.QueryPool.Handle);
        ulong elapsedTicks = RenderQueryTimestampMath.DeltaTicks(
            timestamps[0], timestamps[1], lease.ValidBits);
        ulong elapsedNanoseconds = RenderQueryTimestampMath.TicksToNanoseconds(
            elapsedTicks, lease.TimestampPeriodNanoseconds);
        VulkanTextureUploadService.RecordImportedTextureTransferGpu(
            elapsedNanoseconds / 1_000_000.0);
    }


    private unsafe void RecordImportedTextureTransferUpload(
        CommandBuffer commandBuffer,
        VulkanImportedTexturePendingUpload upload)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = upload.AspectMask,
            BaseMipLevel = 0,
            LevelCount = upload.MipLevels,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        if (!upload.HasRecordedChunk)
        {
            ImageMemoryBarrier uploadBeginBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = upload.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(commandBuffer, PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &uploadBeginBarrier);
        }

        for (int i = 0; i < upload.StagingResources.Length; i++)
        {
            VulkanImportedTextureUploadStagingResource staging = upload.StagingResources[i];
            BufferImageCopy copyRegion = staging.CopyRegion;
            CopyPreparedUploadBufferToImage(
                commandBuffer,
                staging.Buffer,
                upload.Image,
                ImageLayout.TransferDstOptimal,
                ref copyRegion);
        }

        if (upload.CurrentChunkIsFinal)
        {
            ImageMemoryBarrier releaseBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = upload.FinalAccessMask,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = upload.FinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = upload.Image,
                SubresourceRange = range,
            };

            CmdPipelineBarrierTracked(commandBuffer, PipelineStageFlags.TransferBit,
                upload.FinalPipelineStages, 0, 0, null, 0, null, 1, &releaseBarrier);
        }
    }

    private static long CalculateUploadStagingBytes(VulkanImportedTexturePendingUpload upload)
    {
        ulong bytes = 0;
        for (int i = 0; i < upload.StagingResources.Length; i++)
            bytes += upload.StagingResources[i].SizeBytes;
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }
}
