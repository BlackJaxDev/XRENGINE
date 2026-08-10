using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private VulkanTrackedCommandEncoder CreateRecoveryCommandEncoder()
            => new(_commandRuntime);

        private void PrepareRejectedDesktopAbortCommand(
            ref VulkanFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageWasEverPresented,
            out CommandPool commandPool,
            out CommandBuffer commandBuffer,
            out bool replayedPresentationSource)
        {
            commandPool = _commandRuntime.GetThreadGraphicsCommandPool(Api!, _deviceContext, ResourceRuntime);
            commandBuffer = _commandRuntime.AllocateTrackedCommandBuffer(
                Api!,
                _deviceContext,
                ResourceRuntime,
                commandPool,
                CommandBufferLevel.Primary,
                "swapchain abort present transition command buffer");
            _commandRuntime.CommandBuffers.RegisterImageIndex(
                commandBuffer,
                attempt.ImageIndex);
            BeginRejectedDesktopTransition(
                ref attempt,
                commandBuffer,
                in policy,
                imageWasEverPresented,
                out replayedPresentationSource);
        }

        private void BeginRejectedDesktopTransition(
            ref VulkanFrameAttempt attempt,
            CommandBuffer commandBuffer,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageWasEverPresented,
            out bool replayedPresentationSource)
        {
            replayedPresentationSource = false;
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.FrameRecovery");
            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) !=
                Result.Success)
                throw new InvalidOperationException(
                    "Failed to begin swapchain abort-present transition command buffer.");

            VulkanTrackedCommandEncoder encoder = CreateRecoveryCommandEncoder();
            _commandRuntime.ResetBindState(encoder, commandBuffer);
            if (policy.Disposition == ERejectedDesktopFrameDisposition.PresentLastCompletedContent)
                replayedPresentationSource = TryRecordRejectedDesktopPresentationReplay(
                    ref attempt,
                    commandBuffer);

            if (!replayedPresentationSource && policy.ShouldClearBeforePresent)
                RecordRejectedDesktopInitializationClear(
                    attempt.ImageIndex,
                    commandBuffer,
                    imageWasEverPresented);

            if (encoder.End(commandBuffer, cacheVariant: false) != Result.Success)
                throw new InvalidOperationException(
                    "Failed to end swapchain abort-present transition command buffer.");
        }

        private bool TryRecordRejectedDesktopPresentationReplay(
            ref VulkanFrameAttempt attempt,
            CommandBuffer commandBuffer)
        {
            VulkanPresentationSourceTuple source =
                _windowPresentSource.CaptureAnyCompleteBinding();
                if (!ResourceRuntime.TryValidatePresentationSourceForReplay(
                    source,
                    out string unavailableReason))
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecoveryReplayUnavailable",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Rejected-frame presentation replay is unavailable for image {0}: {1}.",
                    attempt.ImageIndex,
                    unavailableReason);
                return false;
            }

            Image desktopImage = OutputRuntime.Desktop.Images is not null &&
                attempt.ImageIndex < OutputRuntime.Desktop.Images.Length
                    ? OutputRuntime.Desktop.Images[attempt.ImageIndex]
                    : default;
            ImageSubresourceRange colorRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            _commandRuntime.Synchronization.TryGetSubmittedImageLayout(
                desktopImage,
                in colorRange,
                out ImageLayout desktopInitialLayout);
            VulkanSwapchainRecordingTargetInput targetInput = new(
                attempt.ImageIndex,
                OpenXrTargetContext: null,
                OutputRuntime.DesktopDepthResources,
                OpenXrInitialColorLayout: ImageLayout.Undefined,
                DesktopInitialColorLayout: desktopInitialLayout);
            SwapchainRecordingTarget target = OutputRuntime.ResolveRecordingTarget(
                in targetInput);
            if (!target.IsValid)
                return false;

            bool recorded = RecordRejectedDesktopPresentationBlit(
                commandBuffer,
                source,
                in target,
                new VulkanTrackedCommandEncoder(_commandRuntime));
            if (!recorded)
                return false;

            TransitionRejectedDesktopReplayTargetToPresent(commandBuffer, in target);
            return true;
        }

        private static bool RecordRejectedDesktopPresentationBlit(
            CommandBuffer commandBuffer,
            in VulkanPresentationSourceTuple source,
            in SwapchainRecordingTarget target,
            VulkanTrackedCommandEncoder encoder)
        {
            if (source.Image.Handle == 0 ||
                source.Width == 0 ||
                source.Height == 0 ||
                target.Image.Handle == 0 ||
                target.Extent.Width == 0 ||
                target.Extent.Height == 0)
            {
                return false;
            }

            ImageSubresourceRange sourceRange = new()
            {
                AspectMask = source.Aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            ImageSubresourceRange targetRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            ImageLayout sourceInitialLayout = source.ExpectedLayout == ImageLayout.Undefined
                ? ImageLayout.ShaderReadOnlyOptimal
                : source.ExpectedLayout;
            ImageLayout targetInitialLayout = target.InitialColorLayout;
            ImageMemoryBarrier* toTransfer = stackalloc ImageMemoryBarrier[2];
            toTransfer[0] = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = sourceInitialLayout,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = sourceRange,
            };
            toTransfer[1] = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = targetInitialLayout == ImageLayout.Undefined
                    ? 0
                    : AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = targetInitialLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = targetRange,
            };
            encoder.PipelineBarrier(
                commandBuffer,
                PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                2,
                toTransfer);

            ImageBlit region = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = source.Aspect,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            region.SrcOffsets.Element1 = new Offset3D(
                checked((int)source.Width),
                checked((int)source.Height),
                1);
            region.DstOffsets.Element1 = new Offset3D(
                checked((int)target.Extent.Width),
                checked((int)target.Extent.Height),
                1);
            encoder.BlitImage(
                commandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                target.Image,
                ImageLayout.TransferDstOptimal,
                ref region,
                Filter.Linear);

            ImageMemoryBarrier* afterTransfer = stackalloc ImageMemoryBarrier[2];
            afterTransfer[0] = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = sourceInitialLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = sourceRange,
            };
            afterTransfer[1] = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit |
                    AccessFlags.ColorAttachmentWriteBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = targetRange,
            };
            encoder.PipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                0,
                null,
                0,
                null,
                2,
                afterTransfer);
            return true;
        }

        private void TransitionRejectedDesktopReplayTargetToPresent(
            CommandBuffer commandBuffer,
            in SwapchainRecordingTarget target)
        {
            ImageMemoryBarrier toPresent = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            CreateRecoveryCommandEncoder().PipelineBarrier(
                commandBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private void RecordRejectedDesktopInitializationClear(
            uint imageIndex,
            CommandBuffer commandBuffer,
            bool imageWasEverPresented)
        {
            Image swapchainImage = OutputRuntime.Desktop.Images![imageIndex];
            ImageSubresourceRange clearRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            ImageMemoryBarrier toTransfer = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = imageWasEverPresented
                    ? ImageLayout.PresentSrcKhr
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = swapchainImage,
                SubresourceRange = clearRange,
            };
            CreateRecoveryCommandEncoder().PipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            // Use a visibly distinct recovery background. A black clear is
            // indistinguishable from a dead renderer when no prior swapchain
            // content survived a resize.
            ClearColorValue clearColor =
                new(0.06f, 0.015f, 0.08f, 1.0f);
            CreateRecoveryCommandEncoder().ClearColorImage(
                commandBuffer,
                swapchainImage,
                ImageLayout.TransferDstOptimal,
                ref clearColor,
                1,
                ref clearRange);

            ImageMemoryBarrier toPresent = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = swapchainImage,
                SubresourceRange = clearRange,
            };
            CreateRecoveryCommandEncoder().PipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private bool TryRecordRejectedDesktopRecoveryOverlay(
        ref VulkanFrameAttempt attempt,
            VulkanImGuiFrameSnapshot? snapshot,
            CommandBuffer predecessorCommandBuffer,
            out CommandBuffer overlayCommandBuffer)
        {
            overlayCommandBuffer = default;
            if (snapshot is null)
                return false;

            try
            {
                bool recorded = TryRecordImGuiOverlay(
                    attempt.ImageIndex,
                    snapshot,
                    ImageLayout.PresentSrcKhr,
                    predecessorCommandBuffer,
                    out overlayCommandBuffer);
                if (recorded)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.RecoveryOverlay",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Recorded ImGui over the rejected-frame recovery background for image {0}.",
                        attempt.ImageIndex);
                }

                return recorded;
            }
            catch (Exception ex)
            {
                overlayCommandBuffer = default;
                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecoveryOverlayFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Rejected-frame ImGui recovery overlay failed; presenting the recovery background instead. {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
        }

        private void ReleaseUnsubmittedRejectedDesktopAbortCommand(
            CommandPool commandPool,
            ref CommandBuffer commandBuffer,
            bool submitted)
        {
            if (submitted ||
                commandBuffer.Handle == 0 ||
                commandPool.Handle == 0 ||
                _deviceLost)
                return;

            _commandRuntime.FreeTrackedCommandBuffer(
                Api,
                _deviceContext.Device,
                ResourceRuntime,
                CurrentFrameSlot,
                commandPool,
                ref commandBuffer,
                "FrameLoop.AbortPresent");
            _commandRuntime.RemoveCommandBufferState(commandBuffer);
        }
    }
}
