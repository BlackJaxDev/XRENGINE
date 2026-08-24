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

    internal unsafe bool TrySubmitImportedTextureUploadToTransferQueue(
        VulkanImportedTexturePendingUpload upload,
        out VulkanSubmittedImportedTextureUpload? submitted,
        out string? failureReason)
    {
        submitted = null;
        failureReason = null;

        if (_deviceLost)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        if (!RenderDiagnosticsFlags.VkTextureUploadTransferQueue)
        {
            failureReason = "XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE is disabled";
            return false;
        }

        QueueFamilyIndices families = _deviceContext.QueueFamilies;
        uint graphicsFamily = families.GraphicsFamilyIndex ?? 0u;
        uint transferFamily = families.TransferFamilyIndex ?? graphicsFamily;
        if (_deviceContext.TransferQueue.Handle == 0 || transferFamily == graphicsFamily)
        {
            failureReason = "no dedicated transfer queue family is available";
            return false;
        }

        if (!upload.TryValidateCopyRegions(out string? validationFailure))
        {
            failureReason = validationFailure;
            return false;
        }

        CommandPool pool = GetThreadTransferCommandPool();
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        try
        {
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

            ResetCommandBufferBindState(commandBuffer);
            RecordImportedTextureTransferUpload(commandBuffer, upload);

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
            VulkanSubmittedImportedTextureUpload acceptedOwner = new(
                upload,
                commandBuffer,
                pool,
                fence,
                requiresGraphicsAcquire: true,
                transferFamily,
                graphicsFamily,
                TextureRuntimeDiagnostics.StartTiming(),
                CalculateUploadStagingBytes(upload));

            VulkanSubmissionReceipt submitReceipt;
            submitReceipt = SubmitToQueueTrackedWithDisposition(
                DeviceContext.TransferQueue,
                ref submitInfo,
                fence,
                new VulkanSubmissionDiagnosticContext
                {
                    SubmissionKind = "TextureUpload.Transfer",
                    QueueKind = "Transfer",
                    CommandBufferCount = 1,
                    FirstCommandBufferHandle = (ulong)commandBuffer.Handle,
                    FenceHandle = fence.Handle,
                },
                out _,
                out _,
                "TextureUpload.Transfer");

            if (!submitReceipt.SubmissionAccepted)
            {
                failureReason = $"transfer queue submit failed ({submitReceipt.Result})";
                return false;
            }

            // Native submission has accepted the command buffer and staging sources. Transfer
            // those handles to the submitted-work record before any later managed publication.
            submitted = acceptedOwner;
            commandBuffer = default;
            fence = default;
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
        }
    }

    internal bool TryPollImportedTextureTransfer(
        VulkanSubmittedImportedTextureUpload submitted,
        out bool complete,
        out string? failureReason)
    {
        complete = false;
        failureReason = null;

        if (_deviceLost)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        Result result = Api!.GetFenceStatus(_deviceContext.Device, submitted.Fence);
        if (result == Result.Success)
        {
            CompleteTrackedFence(submitted.Fence);
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

    internal unsafe bool CompleteSubmittedImportedTextureUpload(
        VulkanSubmittedImportedTextureUpload submitted,
        out string? failureReason)
    {
        failureReason = null;
        if (_deviceLost)
        {
            failureReason = "Vulkan device is lost";
            return false;
        }

        if (submitted.RequiresGraphicsAcquire)
            RecordImportedTextureGraphicsAcquire(submitted);

        CommandBuffer commandBuffer = submitted.CommandBuffer;
        if (submitted.Fence.Handle != 0)
        {
            CompleteTrackedFence(submitted.Fence);
            Api!.DestroyFence(_deviceContext.Device, submitted.Fence, null);
        }
        if (commandBuffer.Handle != 0)
            FreeVulkanCommandBufferTracked(submitted.CommandPool, ref commandBuffer, "TextureUpload.TransferComplete");
        RemoveCommandBufferBindState(submitted.CommandBuffer);
        return true;
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

        CmdPipelineBarrierTracked(
            commandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &uploadBeginBarrier);

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

        ImageMemoryBarrier releaseBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = 0,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = upload.Image,
            SubresourceRange = range,
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.BottomOfPipeBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &releaseBarrier);
    }

    private unsafe void RecordImportedTextureGraphicsAcquire(VulkanSubmittedImportedTextureUpload submitted)
    {
        VulkanImportedTexturePendingUpload upload = submitted.Upload;
        using VulkanCommandRuntime.CommandScope graphicsScope = _commandRuntime.NewCommandScope();
        ImageSubresourceRange range = new()
        {
            AspectMask = upload.AspectMask,
            BaseMipLevel = 0,
            LevelCount = upload.MipLevels,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageMemoryBarrier acquireBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = upload.Image,
            SubresourceRange = range,
        };

        CmdPipelineBarrierTracked(
            graphicsScope.CommandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &acquireBarrier);
    }

    private static long CalculateUploadStagingBytes(VulkanImportedTexturePendingUpload upload)
    {
        ulong bytes = 0;
        for (int i = 0; i < upload.StagingResources.Length; i++)
            bytes += upload.StagingResources[i].SizeBytes;
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }
}
