using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool HasDedicatedTextureUploadTransferQueue
    {
        get
        {
            QueueFamilyIndices families = FamilyQueueIndices;
            return transferQueue.Handle != 0
                && families.GraphicsFamilyIndex.HasValue
                && families.TransferFamilyIndex.HasValue
                && families.TransferFamilyIndex.Value != families.GraphicsFamilyIndex.Value;
        }
    }

    internal bool TrySubmitImportedTextureUploadToTransferQueue(
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

        QueueFamilyIndices families = FamilyQueueIndices;
        uint graphicsFamily = families.GraphicsFamilyIndex ?? 0u;
        uint transferFamily = families.TransferFamilyIndex ?? graphicsFamily;
        if (transferQueue.Handle == 0 || transferFamily == graphicsFamily)
        {
            failureReason = "no dedicated transfer queue family is available";
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

            Result allocateResult = Api!.AllocateCommandBuffers(device, ref allocateInfo, out commandBuffer);
            if (allocateResult != Result.Success || commandBuffer.Handle == 0)
            {
                failureReason = $"failed to allocate transfer command buffer ({allocateResult})";
                return false;
            }

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Result beginResult = Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);
            if (beginResult != Result.Success)
            {
                failureReason = $"failed to begin transfer command buffer ({beginResult})";
                return false;
            }

            ResetCommandBufferBindState(commandBuffer);
            RecordImportedTextureTransferUpload(commandBuffer, upload, transferFamily, graphicsFamily);

            Result endResult = Api!.EndCommandBuffer(commandBuffer);
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
            Result fenceResult = Api!.CreateFence(device, ref fenceCreateInfo, null, out fence);
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

            Result submitResult;
            lock (_oneTimeSubmitLock)
                submitResult = SubmitToQueueTracked(transferQueue, ref submitInfo, fence);

            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost();

                failureReason = $"transfer queue submit failed ({submitResult})";
                return false;
            }

            submitted = new VulkanSubmittedImportedTextureUpload(
                upload,
                commandBuffer,
                pool,
                fence,
                requiresGraphicsAcquire: true,
                transferFamily,
                graphicsFamily,
                TextureRuntimeDiagnostics.StartTiming(),
                CalculateUploadStagingBytes(upload));
            commandBuffer = default;
            fence = default;
            return true;
        }
        finally
        {
            if (fence.Handle != 0)
                Api!.DestroyFence(device, fence, null);

            if (commandBuffer.Handle != 0)
            {
                Api!.FreeCommandBuffers(device, pool, 1, ref commandBuffer);
                RemoveCommandBufferBindState(commandBuffer);
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

        Result result = Api!.GetFenceStatus(device, submitted.Fence);
        if (result == Result.Success)
        {
            complete = true;
            return true;
        }

        if (result == Result.NotReady || result == Result.Timeout)
            return true;

        if (result == Result.ErrorDeviceLost)
            MarkDeviceLost();

        failureReason = $"transfer upload fence status failed ({result})";
        return false;
    }

    internal bool CompleteSubmittedImportedTextureUpload(
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
            Api!.DestroyFence(device, submitted.Fence, null);
        if (commandBuffer.Handle != 0)
            Api!.FreeCommandBuffers(device, submitted.CommandPool, 1, ref commandBuffer);
        RemoveCommandBufferBindState(submitted.CommandBuffer);
        return true;
    }

    private void RecordImportedTextureTransferUpload(
        CommandBuffer commandBuffer,
        VulkanImportedTexturePendingUpload upload,
        uint transferFamily,
        uint graphicsFamily)
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
            Api!.CmdCopyBufferToImage(
                commandBuffer,
                staging.Buffer,
                upload.Image,
                ImageLayout.TransferDstOptimal,
                1,
                ref copyRegion);
        }

        ImageMemoryBarrier releaseBarrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = 0,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = transferFamily,
            DstQueueFamilyIndex = graphicsFamily,
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

    private void RecordImportedTextureGraphicsAcquire(VulkanSubmittedImportedTextureUpload submitted)
    {
        VulkanImportedTexturePendingUpload upload = submitted.Upload;
        using CommandScope graphicsScope = NewCommandScope();
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
            SrcQueueFamilyIndex = submitted.TransferQueueFamily,
            DstQueueFamilyIndex = submitted.GraphicsQueueFamily,
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
