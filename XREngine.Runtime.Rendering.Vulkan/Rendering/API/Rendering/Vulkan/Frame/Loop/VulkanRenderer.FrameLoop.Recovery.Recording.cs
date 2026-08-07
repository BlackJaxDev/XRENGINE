using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private void PrepareRejectedDesktopAbortCommand(
            ref VulkanFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageWasEverPresented,
            out CommandPool commandPool,
            out CommandBuffer commandBuffer,
            out bool replayedPresentationSource)
        {
            commandPool = GetThreadCommandPool();
            commandBuffer = AllocateCommandBuffer(
                CommandBufferLevel.Primary,
                "swapchain abort present transition command buffer",
                commandPool);
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

            ResetCommandBufferBindState(commandBuffer);
            if (policy.Disposition == ERejectedDesktopFrameDisposition.PresentLastCompletedContent)
                replayedPresentationSource = TryRecordRejectedDesktopPresentationReplay(
                    ref attempt,
                    commandBuffer);

            if (!replayedPresentationSource && policy.ShouldClearBeforePresent)
                RecordRejectedDesktopInitializationClear(
                    attempt.ImageIndex,
                    commandBuffer,
                    imageWasEverPresented);

            if (EndCommandBufferTracked(
                    commandBuffer,
                    cacheVariant: false) != Result.Success)
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

            SwapchainRecordingTarget target = ResolveSwapchainRecordingTarget(
                attempt.ImageIndex,
                openXrTargetContext: null);
            if (!target.IsValid)
                return false;

            bool recorded = RecordPresentationSourceBlit(
                commandBuffer,
                attempt.ImageIndex,
                source,
                in target,
                VulkanBarrierPlanner.SwapchainPassIndex,
                source.Context);
            if (!recorded)
                return false;

            TransitionRejectedDesktopReplayTargetToPresent(commandBuffer, in target);
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
            CmdPipelineBarrierTracked(
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
            CmdPipelineBarrierTracked(
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
            CmdClearColorImageTracked(
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
                bool recorded = TryRecordImGuiOverlayCommandBuffer(
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
