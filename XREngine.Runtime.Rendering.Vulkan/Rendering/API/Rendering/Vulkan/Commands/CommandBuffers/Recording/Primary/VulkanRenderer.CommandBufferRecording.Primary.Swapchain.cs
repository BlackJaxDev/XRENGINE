using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanCommandRuntime
    {

        private ImageLayout ResolveCurrentSwapchainColorLayout(scoped ref PrimaryCommandBufferRecordingState recordingState)
            => recordingState.SwapchainFinalLayout;

        private static PipelineStageFlags ResolveSwapchainLayoutStage(ImageLayout layout)
            => layout switch
            {
                // The acquired image semaphore is waited at graphics stages. Put
                // the first layout transition in that wait scope as well.
                ImageLayout.Undefined => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                _ => PipelineStageFlags.AllCommandsBit,
            };

        private static AccessFlags ResolveSwapchainLayoutAccess(ImageLayout layout)
            => layout switch
            {
                ImageLayout.Undefined => 0,
                ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.PresentSrcKhr => 0,
                _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

        private void EmitPlannedSwapchainBarriers(scoped ref PrimaryCommandBufferRecordingState recordingState,
            CommandBuffer targetCommandBuffer,
            IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier>? plannedBarriers)
        {
            if (plannedBarriers is null || plannedBarriers.Count == 0 || !recordingState.SwapchainTarget.IsValid)
                return;

            for (int i = 0; i < plannedBarriers.Count; i++)
            {
                VulkanBarrierPlanner.PlannedSwapchainBarrier planned = plannedBarriers[i];
                ImageLayout liveOldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState);
                ImageLayout nextLayout = planned.Next.Layout;

                if (nextLayout == ImageLayout.Undefined)
                    continue;

                if (liveOldLayout != nextLayout)
                {
                    PipelineStageFlags srcStages = ResolveSwapchainLayoutStage(liveOldLayout);
                    PipelineStageFlags dstStages = NormalizePipelineStages(planned.Next.StageMask);
                    ImageMemoryBarrier barrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = FilterAccessFlagsForStages(ResolveSwapchainLayoutAccess(liveOldLayout), srcStages),
                        DstAccessMask = FilterAccessFlagsForStages(planned.Next.AccessMask, dstStages),
                        OldLayout = liveOldLayout,
                        NewLayout = nextLayout,
                        SrcQueueFamilyIndex = planned.SrcQueueFamilyIndex,
                        DstQueueFamilyIndex = planned.DstQueueFamilyIndex,
                        Image = recordingState.SwapchainTarget.Image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            BaseMipLevel = 0,
                            LevelCount = 1,
                            BaseArrayLayer = 0,
                            LayerCount = 1
                        }
                    };

                    CmdPipelineBarrierTracked(
                        targetCommandBuffer,
                        srcStages,
                        dstStages,
                        DependencyFlags.None,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &barrier);
                }

                recordingState.SwapchainInColorAttachmentLayout = nextLayout == ImageLayout.ColorAttachmentOptimal;
                recordingState.SwapchainFinalLayout = nextLayout;
            }
        }

        private void ApplyPipelineOverride(scoped ref PrimaryCommandBufferRecordingState recordingState, in FrameOpContext context)
        {
            if (recordingState.ActivePipelineOverrideScopeSet)
                recordingState.ActivePipelineOverrideScope.Dispose();
            recordingState.ActivePipelineOverrideScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(context.PipelineInstance);
            recordingState.ActivePipelineOverrideScopeSet = true;
        }

        private void TransitionSwapchainToPresent(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            if (!recordingState.SwapchainInColorAttachmentLayout || !recordingState.SwapchainTarget.IsValid)
                return;

            if (recordingState.SwapchainFinalTargetLayout == ImageLayout.ColorAttachmentOptimal)
            {
                recordingState.SwapchainInColorAttachmentLayout = false;
                recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                return;
            }

            ImageMemoryBarrier presentBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = recordingState.SwapchainFinalTargetLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = recordingState.SwapchainTarget.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            CmdPipelineBarrierTracked(
                recordingState.CommandBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &presentBarrier);
            recordingState.SwapchainPresentTransitions++;
            recordingState.SwapchainInColorAttachmentLayout = false;
            recordingState.SwapchainFinalLayout = recordingState.SwapchainFinalTargetLayout;
        }

        internal void EnsureSwapchainColorAttachmentLayoutForBlit(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            if (!recordingState.SwapchainTarget.IsValid)
                return;

            ImageLayout oldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState);
            if (oldLayout == ImageLayout.ColorAttachmentOptimal)
            {
                recordingState.SwapchainInColorAttachmentLayout = true;
                recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                return;
            }

            ImageMemoryBarrier colorBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = ResolveSwapchainLayoutAccess(oldLayout),
                DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = recordingState.SwapchainTarget.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            CmdPipelineBarrierTracked(
                recordingState.CommandBuffer,
                ResolveSwapchainLayoutStage(oldLayout),
                PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &colorBarrier);

            recordingState.SwapchainInColorAttachmentLayout = true;
            recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
        }

        private void TransitionUnwrittenSwapchainToPresent(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            if (!recordingState.TransitionSwapchainToPresent || !recordingState.SwapchainTarget.IsValid)
                return;

            if (recordingState.SwapchainInColorAttachmentLayout)
            {
                TransitionSwapchainToPresent(ref recordingState);
                return;
            }

            ImageLayout oldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState);
            if (oldLayout == ImageLayout.PresentSrcKhr)
            {
                recordingState.SwapchainFinalLayout = ImageLayout.PresentSrcKhr;
                return;
            }

            ImageMemoryBarrier presentBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = ResolveSwapchainLayoutAccess(oldLayout),
                DstAccessMask = 0,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = recordingState.SwapchainTarget.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            CmdPipelineBarrierTracked(
                recordingState.CommandBuffer,
                ResolveSwapchainLayoutStage(oldLayout),
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &presentBarrier);
            recordingState.SwapchainFinalLayout = ImageLayout.PresentSrcKhr;
        }

        private bool TryRefreshUnwrittenSwapchainFromLastWindowPresentSource(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            VulkanPresentationSourceTuple presentationSource =
                recordingState.PresentationSource;
            XRFrameBuffer? sourceFrameBuffer = presentationSource.FrameBuffer;
            string? unavailableReason = !presentationSource.HasLogicalSource
                ? "no published presentation source"
                : !recordingState.SwapchainTarget.IsValid
                    ? "swapchain target is invalid"
                    : !ResourceRuntime.TryValidatePresentationSourceForReplay(
                        presentationSource,
                        out string tupleFailure)
                        ? tupleFailure
                    : presentationSource.Width == 0 || presentationSource.Height == 0
                        ? $"published native source has zero size {presentationSource.Width}x{presentationSource.Height}"
                        : recordingState.SwapchainRecordExtent.Width == 0 || recordingState.SwapchainRecordExtent.Height == 0
                            ? $"swapchain record extent is zero {recordingState.SwapchainRecordExtent.Width}x{recordingState.SwapchainRecordExtent.Height}"
                            : null;
            if (unavailableReason is not null)
            {
                Debug.VulkanEvery(
                    $"Vulkan.LastPresentRefresh.Unavailable.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Unable to refresh unwritten swapchain image from last present source: {0}.",
                    unavailableReason);
                return false;
            }

            EnsureSwapchainColorAttachmentLayoutForBlit(ref recordingState);

            int passIndex = recordingState.ActivePassIndex != int.MinValue
                ? recordingState.ActivePassIndex
                : VulkanBarrierPlanner.SwapchainPassIndex;
            FrameOpContext blitContext = presentationSource.LogicalEpoch != 0
                ? presentationSource.Context
                : recordingState.HasActiveContext
                    ? recordingState.ActiveContext
                    : recordingState.InitialContext;
            CmdBeginLabel(recordingState.CommandBuffer, "RefreshSwapchainFromLastPresentSource");
            bool blitRecorded = RecordPresentationSourceBlit(
                recordingState.CommandBuffer,
                recordingState.ImageIndex,
                presentationSource,
                in recordingState.SwapchainTarget,
                passIndex,
                blitContext);
            CmdEndLabel(recordingState.CommandBuffer);
            if (!blitRecorded)
            {
                Debug.VulkanEvery(
                    $"Vulkan.LastPresentRefresh.BlitRejected.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Unable to refresh unwritten swapchain image from last present source: blit from '{0}' was not recorded.",
                    sourceFrameBuffer?.Name ?? presentationSource.ColorTexture?.Name ?? "<native source>");
                return false;
            }

            recordingState.SwapchainWrittenOutsideRenderPass = true;
            recordingState.SwapchainInColorAttachmentLayout = true;
            recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
            recordingState.SwapchainWriteCount++;
            recordingState.ActualSwapchainWriteCount++;
            recordingState.SwapchainBlitWrites++;
            recordingState.SceneSwapchainWriters++;
            MarkSwapchainStaticWriter(ref recordingState,
                "LastPresentSourceBlit",
                $"refreshed acquired swapchain image from '{sourceFrameBuffer?.Name ?? presentationSource.ColorTexture?.Name ?? "<native source>"}'",
                passIndex,
                recordingState.Ops.Length,
                blitContext.PipelineIdentity);
            return true;
        }
    }
}
