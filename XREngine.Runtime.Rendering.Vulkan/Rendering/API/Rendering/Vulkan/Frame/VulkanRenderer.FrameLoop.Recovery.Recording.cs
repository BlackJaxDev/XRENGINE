using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private void PrepareRejectedDesktopAbortCommand(
            ref DesktopFrameAttempt attempt,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageWasEverPresented,
            out CommandPool commandPool,
            out CommandBuffer commandBuffer)
        {
            commandPool = GetThreadCommandPool();
            commandBuffer = AllocateCommandBuffer(
                CommandBufferLevel.Primary,
                "swapchain abort present transition command buffer",
                commandPool);
            RegisterCommandBufferImageIndex(
                commandBuffer,
                attempt.ImageIndex);
            BeginRejectedDesktopTransition(
                ref attempt,
                commandBuffer,
                in policy,
                imageWasEverPresented);
        }

        private void BeginRejectedDesktopTransition(
            ref DesktopFrameAttempt attempt,
            CommandBuffer commandBuffer,
            in RejectedDesktopFramePolicyDecision policy,
            bool imageWasEverPresented)
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) !=
                Result.Success)
            {
                throw new InvalidOperationException(
                    "Failed to begin swapchain abort-present transition command buffer.");
            }

            ResetCommandBufferBindState(commandBuffer);
            if (policy.ShouldClearBeforePresent)
            {
                RecordRejectedDesktopInitializationClear(
                    attempt.ImageIndex,
                    commandBuffer,
                    imageWasEverPresented);
            }

            if (EndCommandBufferTracked(
                    commandBuffer,
                    cacheVariant: false) != Result.Success)
            {
                throw new InvalidOperationException(
                    "Failed to end swapchain abort-present transition command buffer.");
            }
        }

        private void RecordRejectedDesktopInitializationClear(
            uint imageIndex,
            CommandBuffer commandBuffer,
            bool imageWasEverPresented)
        {
            Image swapchainImage = swapChainImages![imageIndex];
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

            ClearColorValue clearColor =
                new(0.0f, 0.0f, 0.0f, 1.0f);
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

        private void ReleaseUnsubmittedRejectedDesktopAbortCommand(
            CommandPool commandPool,
            ref CommandBuffer commandBuffer,
            bool submitted)
        {
            if (submitted ||
                commandBuffer.Handle == 0 ||
                commandPool.Handle == 0 ||
                _deviceLost)
            {
                return;
            }

            FreeVulkanCommandBufferTracked(
                commandPool,
                ref commandBuffer,
                "FrameLoop.AbortPresent");
            RemoveCommandBufferBindState(commandBuffer);
        }
    }
}
