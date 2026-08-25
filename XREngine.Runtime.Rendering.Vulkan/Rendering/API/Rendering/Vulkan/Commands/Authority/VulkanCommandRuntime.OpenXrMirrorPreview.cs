using Silk.NET.Vulkan;
using System.Diagnostics;
using XREngine.Data.Colors;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns native OpenXR mirror/preview command recording and short-lived command
/// artifacts. Callers must freeze every image, format, extent, and layout before
/// crossing this boundary; this authority never samples output or planner state.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal VulkanQueueOperationLease EnterSerializedOpenXrCommandSection(
        string operation)
    {
        long waitStart = Stopwatch.GetTimestamp();
        VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(
            CommandBuffers.OneTimeSubmitGate,
            DeviceContext.StateMachine,
            FrameTelemetry);
        if (IsOpenXrTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXrVulkan] serialized command section operation={0} acquired={1} waitMs={2:F3}",
                operation,
                lease.Acquired,
                Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds);
        }

        return lease;
    }

    internal void DestroyOpenXrPrimaryCommandArtifacts()
    {
        lock (CommandBuffers.SubmissionStateGate)
            lock (CommandBuffers.OpenXrPrimaryOwnersGate)
            {
                foreach (PrimaryCommandArtifactOwner owner in
                         CommandBuffers.OpenXrPrimaryOwners.Values)
                {
                    ReleaseOpenXrOwnedPrimaryArtifact(
                        owner.PrimaryCommandPool,
                        owner.PrimaryCommandBuffer,
                        owner.OwnsPrimaryCommandBuffer);
                    ReleaseOpenXrOwnedPrimaryArtifact(
                        owner.DynamicUiSecondaryCommandPool,
                        owner.DynamicUiSecondaryCommandBuffer,
                        owner.OwnsDynamicUiSecondaryCommandBuffer);
                }

                CommandBuffers.OpenXrPrimaryOwners.Clear();
            }

        DestroyOpenXrEyeCommandPools();
    }

    internal void MarkAllOpenXrPrimaryCommandArtifactsDirty()
    {
        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            foreach (PrimaryCommandArtifactOwner owner in
                     CommandBuffers.OpenXrPrimaryOwners.Values)
            {
                owner.Dirty = true;
            }
        }
    }

    internal void MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
        in OpenXrRecordedEyeCommandBuffer recorded,
        string reason)
    {
        if (!recorded.OwnedByOpenXrPrimaryCache ||
            recorded.CommandBuffer.Handle == 0)
        {
            return;
        }

        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            foreach (PrimaryCommandArtifactOwner owner in
                     CommandBuffers.OpenXrPrimaryOwners.Values)
            {
                if (owner.PrimaryCommandBuffer.Handle !=
                    recorded.CommandBuffer.Handle)
                {
                    continue;
                }

                owner.Dirty = true;
                owner.DirtyReason = reason;
                return;
            }
        }
    }

    private void ReleaseOpenXrOwnedPrimaryArtifact(
        CommandPool pool,
        CommandBuffer commandBuffer,
        bool owned)
    {
        if (commandBuffer.Handle == 0)
            return;

        if (!owned || !DeviceContext.IsOperational || pool.Handle == 0)
        {
            RemoveCommandBufferState(commandBuffer);
            return;
        }

        CommandBuffer releasing = commandBuffer;
        FreeCompletedSynchronousCommandBuffer(
            pool,
            ref releasing,
            "OpenXR.OwnedPrimaryArtifact");
    }

    internal bool TryRecordPreparedOpenXrMirror(
        in VulkanPreparedPrimaryCommandInput commandInput,
        in VulkanOpenXrFrameContext frameContext,
        uint openXrViewIndex,
        uint openXrImageIndex,
        uint frameDataSlotIndex,
        ulong frameOpsSignature,
        ulong plannerRevision,
        ulong frameOpContextId,
        ulong resourceGeneration,
        ulong descriptorGeneration,
        out OpenXrRecordedEyeCommandBuffer recorded,
        out VulkanImportedTexturePendingUpload[] recordedUploads)
    {
        recorded = default;
        recordedUploads = [];
        if (!DeviceContext.IsOperational ||
            commandInput.PrimaryCommandBuffer.Handle == 0 ||
            !commandInput.FramePlan.IsSealed)
        {
            return false;
        }

        List<VulkanImportedTexturePendingUpload> uploadBatch =
            ResourceRuntime.Uploads.PublicationState.RecordedForSubmit;
        uploadBatch.Clear();
        try
        {
            VulkanPrimaryCommandRecordingResult result =
                RecordPrimary(in commandInput);
            if (!result.Succeeded)
            {
                ResourceRuntime.Uploads.CancelRecordedSubmitBatch(
                    DeviceContext.State != EVulkanDeviceState.Healthy,
                    result.Reason ?? "OpenXR mirror command recording deferred");
                return false;
            }

            if (uploadBatch.Count != 0)
            {
                recordedUploads = [.. uploadBatch];
                uploadBatch.Clear();
            }

            recorded = new OpenXrRecordedEyeCommandBuffer(
                result.CommandBuffer,
                frameContext,
                openXrViewIndex,
                openXrImageIndex,
                frameDataSlotIndex,
                frameOpsSignature,
                plannerRevision,
                frameOpContextId,
                resourceGeneration,
                descriptorGeneration,
                OwnedByOpenXrPrimaryCache: true);
            return true;
        }
        catch
        {
            ResourceRuntime.Uploads.CancelRecordedSubmitBatch(
                DeviceContext.State != EVulkanDeviceState.Healthy,
                "OpenXR mirror command recording failed");
            throw;
        }
    }

    internal bool ExecuteOpenXrPreviewCopy(in OpenXrEyePreviewCopyPlan plan)
    {
        if (!TryBeginOpenXrTemporaryCommand(
                "OpenXR.PreviewCopy",
                out CommandBuffer commandBuffer,
                out VulkanTrackedCommandEncoder encoder))
        {
            return false;
        }

        EVulkanQueueSubmissionDisposition disposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;
        try
        {
            RecordOpenXrEyeSwapchainPreviewCopy(
                encoder,
                commandBuffer,
                in plan);
            if (!TryEndOpenXrTemporaryCommand(
                    encoder,
                    commandBuffer,
                    "OpenXR.PreviewCopy"))
            {
                return false;
            }

            VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(
                new VulkanOpenXrSubmissionInput(
                    commandBuffer,
                    default,
                    1,
                    default));
            disposition = result.SubmissionDisposition;
            return result.Succeeded;
        }
        finally
        {
            ReleaseOpenXrTemporaryCommandBuffer(commandBuffer, disposition);
        }
    }

    internal bool ExecuteOpenXrMirrorPublish(
        in OpenXrEyeMirrorPublishPlan firstPlan,
        in OpenXrEyeMirrorPublishPlan secondPlan,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;
        if (!TryRecordOpenXrEyeMirrorPublishCommandBuffer(
                in firstPlan,
                in secondPlan,
                out CommandBuffer commandBuffer,
                out firstPreviewCopied,
                out secondPreviewCopied))
        {
            return false;
        }

        EVulkanQueueSubmissionDisposition disposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;
        try
        {
            VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(
                new VulkanOpenXrSubmissionInput(
                    commandBuffer,
                    default,
                    1,
                    default));
            disposition = result.SubmissionDisposition;
            return result.Succeeded;
        }
        finally
        {
            ReleaseOpenXrTemporaryCommandBuffer(commandBuffer, disposition);
        }
    }

    internal bool ExecuteOpenXrDiagnosticClear(
        Image image,
        Extent2D extent,
        ColorF4 color)
    {
        if (image.Handle == 0 || extent.Width == 0 || extent.Height == 0 ||
            !TryBeginOpenXrTemporaryCommand(
                "OpenXR.DiagnosticClear",
                out CommandBuffer commandBuffer,
                out VulkanTrackedCommandEncoder encoder))
        {
            return false;
        }

        EVulkanQueueSubmissionDisposition disposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;
        try
        {
            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                image,
                Format.Undefined,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                ImageAspectFlags.ColorBit);
            ClearColorValue clearColor = new(color.R, color.G, color.B, color.A);
            encoder.ClearColorImage(
                commandBuffer,
                image,
                ImageLayout.TransferDstOptimal,
                ref clearColor,
                1,
                ref range);
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                image,
                Format.Undefined,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);
            if (!TryEndOpenXrTemporaryCommand(
                    encoder,
                    commandBuffer,
                    "OpenXR.DiagnosticClear"))
            {
                return false;
            }

            VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(
                new VulkanOpenXrSubmissionInput(
                    commandBuffer,
                    default,
                    1,
                    default));
            disposition = result.SubmissionDisposition;
            return result.Succeeded;
        }
        finally
        {
            ReleaseOpenXrTemporaryCommandBuffer(commandBuffer, disposition);
        }
    }

    internal bool ExecuteOpenXrMirrorTextureCopy(
        IVkImageDescriptorSource source,
        Image sourceImage,
        Format sourceFormat,
        Extent2D sourceExtent,
        ImageLayout sourceOldLayout,
        ImageAspectFlags sourceAspect,
        uint sourceBaseArrayLayer,
        IVkImageDescriptorSource destination,
        Image destinationImage,
        Extent2D destinationExtent,
        ImageLayout destinationOldLayout,
        ImageAspectFlags destinationAspect,
        bool flipY)
    {
        if (!TryBeginOpenXrTemporaryCommand(
                "OpenXR.MirrorTextureCopy",
                out CommandBuffer commandBuffer,
                out VulkanTrackedCommandEncoder encoder))
        {
            return false;
        }

        EVulkanQueueSubmissionDisposition disposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;
        try
        {
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                sourceImage,
                sourceFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                sourceAspect,
                sourceBaseArrayLayer,
                1);
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                destinationOldLayout,
                ImageLayout.TransferDstOptimal,
                destinationAspect);

            ImageBlit blit = CreateOpenXrMirrorBlit(
                sourceAspect,
                destinationAspect,
                sourceExtent,
                destinationExtent,
                flipY);
            blit.SrcSubresource.BaseArrayLayer = sourceBaseArrayLayer;
            encoder.BlitImage(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                ref blit,
                Filter.Nearest);
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                sourceImage,
                sourceFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                sourceAspect,
                sourceBaseArrayLayer,
                1);
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                destinationAspect);
            if (destination is IVkFrameBufferAttachmentSource destinationAttachment)
                destinationAttachment.UpdateAttachmentTrackedLayout(
                    ImageLayout.ShaderReadOnlyOptimal,
                    0,
                    0);
            if (source is IVkFrameBufferAttachmentSource sourceAttachment)
                sourceAttachment.UpdateAttachmentTrackedLayout(
                    sourceOldLayout,
                    0,
                    checked((int)sourceBaseArrayLayer));

            if (!TryEndOpenXrTemporaryCommand(
                    encoder,
                    commandBuffer,
                    "OpenXR.MirrorTextureCopy"))
            {
                return false;
            }
            VulkanOpenXrSubmissionResult result = SubmitAndWaitOpenXr(
                new VulkanOpenXrSubmissionInput(
                    commandBuffer,
                    default,
                    1,
                    default));
            disposition = result.SubmissionDisposition;
            return result.Succeeded;
        }
        finally
        {
            ReleaseOpenXrTemporaryCommandBuffer(commandBuffer, disposition);
        }
    }

    internal bool TryRecordOpenXrEyeMirrorPublishCommandBuffer(
        in OpenXrEyeMirrorPublishPlan firstPlan,
        in OpenXrEyeMirrorPublishPlan secondPlan,
        out CommandBuffer commandBuffer,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;
        if (!TryBeginOpenXrTemporaryCommand(
                "OpenXR.MirrorPublish",
                out commandBuffer,
                out VulkanTrackedCommandEncoder encoder))
        {
            return false;
        }

        try
        {
            RecordOpenXrEyeMirrorPublish(
                encoder,
                commandBuffer,
                in firstPlan,
                out firstPreviewCopied);
            RecordOpenXrEyeMirrorPublish(
                encoder,
                commandBuffer,
                in secondPlan,
                out secondPreviewCopied);
            if (TryEndOpenXrTemporaryCommand(
                    encoder,
                    commandBuffer,
                    "OpenXR.MirrorPublish"))
            {
                return true;
            }
        }
        catch
        {
            encoder.Abandon(commandBuffer);
            ReleaseOpenXrTemporaryCommandBuffer(
                commandBuffer,
                EVulkanQueueSubmissionDisposition.Completed);
            commandBuffer = default;
            throw;
        }

        ReleaseOpenXrTemporaryCommandBuffer(
            commandBuffer,
            EVulkanQueueSubmissionDisposition.Completed);
        commandBuffer = default;
        return false;
    }

    internal void ReleaseOpenXrTemporaryCommandBuffer(
        CommandBuffer commandBuffer,
        EVulkanQueueSubmissionDisposition disposition)
    {
        if (commandBuffer.Handle == 0)
            return;

        if (disposition == EVulkanQueueSubmissionDisposition.SubmittedIncomplete)
        {
            RemoveCommandBufferState(commandBuffer);
            return;
        }

        CommandBuffer releasing = commandBuffer;
        FreeCompletedSynchronousCommandBuffer(
            Pools.PrimaryGraphics,
            ref releasing,
            "OpenXR.TemporaryCommandBuffer");
    }

    internal void TransitionOpenXrMirrorImage(
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask,
        uint baseArrayLayer = 0,
        uint layerCount = 1)
    {
        VulkanTrackedCommandEncoder encoder = new(this);
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            image,
            format,
            oldLayout,
            newLayout,
            aspectMask,
            baseArrayLayer,
            layerCount);
    }

    private bool TryBeginOpenXrTemporaryCommand(
        string owner,
        out CommandBuffer commandBuffer,
        out VulkanTrackedCommandEncoder encoder)
    {
        commandBuffer = default;
        encoder = new VulkanTrackedCommandEncoder(this);
        if (!DeviceContext.IsOperational || Pools.PrimaryGraphics.Handle == 0)
            return false;

        commandBuffer = AllocateTrackedCommandBuffer(
            Api,
            DeviceContext,
            ResourceRuntime,
            Pools.PrimaryGraphics,
            CommandBufferLevel.Primary,
            owner);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = BeginTrackedCommandBuffer(
            commandBuffer,
            ref beginInfo,
            owner);
        DeviceContext.ObserveNativeResult($"vkBeginCommandBuffer.{owner}", result);
        if (result == Result.Success)
            return true;

        encoder.Abandon(commandBuffer);
        ReleaseOpenXrTemporaryCommandBuffer(
            commandBuffer,
            EVulkanQueueSubmissionDisposition.Completed);
        commandBuffer = default;
        return false;
    }

    private bool TryEndOpenXrTemporaryCommand(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        string owner)
    {
        Result result = encoder.End(commandBuffer, cacheVariant: false);
        DeviceContext.ObserveNativeResult($"vkEndCommandBuffer.{owner}", result);
        return result == Result.Success;
    }

    private void RecordOpenXrEyeSwapchainPreviewCopy(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        in OpenXrEyePreviewCopyPlan plan)
    {
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            plan.SourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            ImageAspectFlags.ColorBit);
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.DestinationImage,
            plan.DestinationSource.DescriptorFormat,
            plan.DestinationOldLayout,
            ImageLayout.TransferDstOptimal,
            plan.DestinationAspect);

        ImageBlit blit = CreateOpenXrMirrorBlit(
            ImageAspectFlags.ColorBit,
            plan.DestinationAspect,
            plan.SourceExtent,
            plan.DestinationExtent,
            plan.FlipY);
        encoder.BlitImage(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.DestinationImage,
            ImageLayout.TransferDstOptimal,
            ref blit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.DestinationImage,
            plan.DestinationSource.DescriptorFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            plan.DestinationAspect);

        if (plan.DestinationSource is IVkFrameBufferAttachmentSource attachment)
            attachment.UpdateAttachmentTrackedLayout(
                ImageLayout.ShaderReadOnlyOptimal,
                0,
                0);
    }

    private void RecordOpenXrEyeMirrorPublish(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        in OpenXrEyeMirrorPublishPlan plan,
        out bool previewCopied)
    {
        previewCopied = false;
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            plan.SourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            plan.SourceAspect);
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.SwapchainImage,
            plan.SwapchainFormat,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.ColorBit);

        ImageBlit swapchainBlit = CreateOpenXrMirrorBlit(
            plan.SourceAspect,
            ImageAspectFlags.ColorBit,
            plan.SourceExtent,
            plan.SwapchainExtent,
            flipDestinationY: false);
        encoder.BlitImage(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.SwapchainImage,
            ImageLayout.TransferDstOptimal,
            ref swapchainBlit,
            Filter.Nearest);
        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.SwapchainImage,
            plan.SwapchainFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);

        if (plan.PreviewSource is not null && plan.PreviewImage.Handle != 0)
        {
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                plan.PreviewImage,
                plan.PreviewSource.DescriptorFormat,
                plan.PreviewOldLayout,
                ImageLayout.TransferDstOptimal,
                plan.PreviewAspect);
            ImageBlit previewBlit = CreateOpenXrMirrorBlit(
                plan.SourceAspect,
                plan.PreviewAspect,
                plan.SourceExtent,
                plan.PreviewExtent,
                plan.FlipPreviewY);
            encoder.BlitImage(
                commandBuffer,
                plan.SourceImage,
                ImageLayout.TransferSrcOptimal,
                plan.PreviewImage,
                ImageLayout.TransferDstOptimal,
                ref previewBlit,
                Filter.Nearest);
            TransitionOpenXrMirrorImage(
                encoder,
                commandBuffer,
                plan.PreviewImage,
                plan.PreviewSource.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                plan.PreviewAspect);
            if (plan.PreviewSource is IVkFrameBufferAttachmentSource previewAttachment)
                previewAttachment.UpdateAttachmentTrackedLayout(
                    ImageLayout.ShaderReadOnlyOptimal,
                    0,
                    0);
            previewCopied = true;
        }

        TransitionOpenXrMirrorImage(
            encoder,
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            plan.SourceOldLayout,
            plan.SourceAspect);
        if (plan.Source is IVkFrameBufferAttachmentSource sourceAttachment)
            sourceAttachment.UpdateAttachmentTrackedLayout(
                plan.SourceOldLayout,
                0,
                0);
    }

    private unsafe void TransitionOpenXrMirrorImage(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask,
        uint baseArrayLayer = 0,
        uint layerCount = 1)
    {
        if (oldLayout == newLayout)
            return;

        OpenXrMirrorBarrierAccess(
            oldLayout,
            out AccessFlags srcAccess,
            out PipelineStageFlags srcStage);
        OpenXrMirrorBarrierAccess(
            newLayout,
            out AccessFlags dstAccess,
            out PipelineStageFlags dstStage);
        ImageSubresourceRange range = new()
        {
            AspectMask = NormalizeOpenXrMirrorAspect(format, aspectMask),
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = baseArrayLayer,
            LayerCount = Math.Max(layerCount, 1u),
        };
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
        ulong generation = ResourceRuntime.GetPublishedGeneration(
            ObjectType.Image,
            image.Handle);
        encoder.RecordImageAccess(
            commandBuffer,
            image,
            in range,
            new VulkanImageAccessState(
                newLayout,
                (PipelineStageFlags2)(ulong)dstStage,
                (AccessFlags2)(ulong)dstAccess,
                Vk.QueueFamilyIgnored,
                newLayout == ImageLayout.ShaderReadOnlyOptimal
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.Undefined,
                unchecked((ulong)Interlocked.Increment(
                    ref FrameTelemetry._vulkanImageLayoutTransitionSerial)),
                generation));
    }

    private static ImageBlit CreateOpenXrMirrorBlit(
        ImageAspectFlags sourceAspect,
        ImageAspectFlags destinationAspect,
        Extent2D sourceExtent,
        Extent2D destinationExtent,
        bool flipDestinationY)
    {
        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = sourceAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = destinationAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets.Element1 = new Offset3D
        {
            X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
            Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
            Z = 1,
        };
        int width = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
        int height = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
        blit.DstOffsets.Element0 = new Offset3D
        {
            X = 0,
            Y = flipDestinationY ? height : 0,
            Z = 0,
        };
        blit.DstOffsets.Element1 = new Offset3D
        {
            X = width,
            Y = flipDestinationY ? 0 : height,
            Z = 1,
        };
        return blit;
    }

    private static ImageAspectFlags NormalizeOpenXrMirrorAspect(
        Format format,
        ImageAspectFlags aspect)
    {
        if (!VulkanDesktopSwapchainService.IsDepthStencilFormatForOutput(format))
            return ImageAspectFlags.ColorBit;

        ImageAspectFlags normalized = aspect &
            (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
        return normalized == ImageAspectFlags.None
            ? ImageAspectFlags.DepthBit
            : normalized;
    }

    private static void OpenXrMirrorBarrierAccess(
        ImageLayout layout,
        out AccessFlags access,
        out PipelineStageFlags stage)
    {
        switch (layout)
        {
            case ImageLayout.Undefined:
                access = 0;
                stage = PipelineStageFlags.TopOfPipeBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                access = AccessFlags.ColorAttachmentReadBit |
                    AccessFlags.ColorAttachmentWriteBit;
                stage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                access = AccessFlags.TransferReadBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.TransferDstOptimal:
                access = AccessFlags.TransferWriteBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
                access = AccessFlags.ShaderReadBit;
                stage = PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.General:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
            default:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
        }
    }
}
