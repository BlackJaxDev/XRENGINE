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
    public unsafe partial class VulkanRenderer
    {

        private void ExecuteDynamicUiBatchTextOverlay(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            if (recordingState.DynamicUiBatchTextOpCount <= 0)
                return;

            CommandBuffer secondaryCommandBuffer = recordingState.DynamicUiBatchTextSecondaryCommandBuffer;
            if (secondaryCommandBuffer.Handle == 0)
                return;

            EndActiveRenderPass(ref recordingState);
            CmdBeginLabel(recordingState.CommandBuffer, "DynamicUIBatchText");
            TransitionSecondaryDescriptorImagesForExecution(recordingState.CommandBuffer, secondaryCommandBuffer);

            try
            {
                bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                    recordingState.SwapchainTarget.IsValid;

                if (useDynamicRendering)
                {
                    ImageLayout colorOldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState);

                    bool loadExistingSwapchainColor =
                        recordingState.SwapchainClearedThisFrame ||
                        recordingState.SwapchainWrittenOutsideRenderPass ||
                        recordingState.ImageWasEverPresentedAtRecordStart;
                    AttachmentLoadOp colorLoadOp = loadExistingSwapchainColor
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear;

                    ImageMemoryBarrier colorBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = 0,
                        DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                        OldLayout = colorOldLayout,
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

                    ImageSubresourceRange depthRange = new()
                    {
                        AspectMask = recordingState.SwapchainTarget.DepthAspect,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    };
                    bool hasRecordedDepthState = TryGetRecordedImageAccessState(
                        recordingState.CommandBuffer,
                        recordingState.SwapchainTarget.DepthImage,
                        depthRange,
                        out VulkanImageAccessState recordedDepthState);
                    ImageLayout depthOldLayout = hasRecordedDepthState
                        ? recordedDepthState.Layout
                        : ImageLayout.Undefined;

                    ImageMemoryBarrier depthBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = hasRecordedDepthState
                            ? (AccessFlags)(ulong)recordedDepthState.AccessMask
                            : AccessFlags.None,
                        DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                        OldLayout = depthOldLayout,
                        NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = recordingState.SwapchainTarget.DepthImage,
                        SubresourceRange = depthRange
                    };

                    ImageMemoryBarrier* preRenderingBarriers = stackalloc ImageMemoryBarrier[2];
                    uint preRenderingBarrierCount = 0;
                    if (colorOldLayout != ImageLayout.ColorAttachmentOptimal)
                        preRenderingBarriers[preRenderingBarrierCount++] = colorBarrier;
                    preRenderingBarriers[preRenderingBarrierCount++] = depthBarrier;

                    CmdPipelineBarrierTracked(
                        recordingState.CommandBuffer,
                        PipelineStageFlags.ColorAttachmentOutputBit |
                            (hasRecordedDepthState
                                ? (PipelineStageFlags)(ulong)recordedDepthState.StageMask
                                : PipelineStageFlags.None),
                        PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        preRenderingBarrierCount,
                        preRenderingBarriers);

                    ClearValue* dynamicClearValues = stackalloc ClearValue[2];
                    ActiveState.WriteClearValues(dynamicClearValues, 2);

                    Span<DynamicRenderingAttachmentPlan> colorAttachmentPlans = stackalloc DynamicRenderingAttachmentPlan[1];
                    colorAttachmentPlans[0] = new DynamicRenderingAttachmentPlan(
                        recordingState.SwapchainTarget.Image,
                        recordingState.SwapchainTarget.ImageView,
                        recordingState.SwapchainTarget.ImageFormat,
                        ImageAspectFlags.ColorBit,
                        colorOldLayout,
                        ImageLayout.ColorAttachmentOptimal,
                        ImageLayout.PresentSrcKhr,
                        colorLoadOp,
                        AttachmentStoreOp.Store,
                        dynamicClearValues[0]);

                    DynamicRenderingAttachmentPlan depthAttachmentPlan = new(
                        recordingState.SwapchainTarget.DepthImage,
                        recordingState.SwapchainTarget.DepthView,
                        recordingState.SwapchainTarget.DepthFormat,
                        recordingState.SwapchainTarget.DepthAspect,
                        depthOldLayout,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        AttachmentLoadOp.Clear,
                        AttachmentStoreOp.DontCare,
                        dynamicClearValues[1]);

                    DynamicRenderingFormatSignature swapchainDynamicRenderingFormats =
                        CreateSwapchainDynamicRenderingFormatSignature(recordingState.SwapchainTarget.ImageFormat, recordingState.SwapchainTarget.DepthFormat);
                    DynamicRenderingScopePlan scopePlan = new(
                        new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = recordingState.SwapchainTarget.Extent
                        },
                        1u,
                        0u,
                        colorAttachmentPlans,
                        depthAttachmentPlan,
                        true,
                        default,
                        false,
                        false,
                        swapchainDynamicRenderingFormats,
                        SampleCountFlags.Count1Bit);

                    BeginDynamicRenderingScope(recordingState.CommandBuffer, in scopePlan, secondaryContents: true);
                    CmdExecuteCommandsTracked(recordingState.CommandBuffer, 1, &secondaryCommandBuffer);
                    CmdEndDynamicRendering(recordingState.CommandBuffer);

                    recordingState.UsedSwapchainDynamicRendering = true;
                    recordingState.SwapchainInColorAttachmentLayout = true;
                    recordingState.SwapchainClearedThisFrame = true;
                }
                else if (OutputRuntime.Desktop.Framebuffers is not null && recordingState.ImageIndex < OutputRuntime.Desktop.Framebuffers.Length)
                {
                    RenderPassBeginInfo renderPassInfo = new()
                    {
                        SType = StructureType.RenderPassBeginInfo,
                        RenderPass = ResourceRuntime.SwapchainLoadRenderPass,
                        Framebuffer = OutputRuntime.Desktop.Framebuffers[recordingState.ImageIndex],
                        RenderArea = new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = recordingState.SwapchainRecordExtent
                        }
                    };

                    const uint attachmentCount = 2;
                    ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
                    ActiveState.WriteClearValues(clearValues, attachmentCount);
                    renderPassInfo.ClearValueCount = attachmentCount;
                    renderPassInfo.PClearValues = clearValues;

                    CmdBeginRenderPassTracked(recordingState.CommandBuffer, &renderPassInfo, SubpassContents.SecondaryCommandBuffers);
                    CmdExecuteCommandsTracked(recordingState.CommandBuffer, 1, &secondaryCommandBuffer);
                    Api!.CmdEndRenderPass(recordingState.CommandBuffer);

                    recordingState.SwapchainClearedThisFrame = true;
                }

                recordingState.SwapchainWriteCount++;
                recordingState.ActualSwapchainWriteCount++;
                recordingState.SwapchainDrawWrites++;
                recordingState.OverlaySwapchainWriters++;
                MarkSwapchainDynamicUiWriter(ref recordingState,
                    "DynamicUIBatchText",
                    recordingState.DynamicUiBatchTextOpCount,
                    recordingState.ActivePassIndex != int.MinValue ? recordingState.ActivePassIndex : VulkanBarrierPlanner.SwapchainPassIndex,
                    recordingState.Ops.Length,
                    recordingState.HasActiveContext ? recordingState.ActiveContext.PipelineIdentity : recordingState.InitialContext.PipelineIdentity);
            }
            finally
            {
                CmdEndLabel(recordingState.CommandBuffer);
            }
        }

        private int EmitPassBarriers(scoped ref PrimaryCommandBufferRecordingState recordingState, int passIndex)
        {
            // Emit any global pending memory barriers that accumulated before recording.
            // After the first pass consumes them they are cleared.
            EmitPendingMemoryBarriers(recordingState.CommandBuffer);

            // Ensure first-use physical-group images are transitioned out of UNDEFINED
            // before any planned pass consumes them.
            EmitInitialImageBarriersForUnknownPass(
                recordingState.CommandBuffer,
                skipDesktopSwapchainImages: recordingState.ExcludeDesktopSwapchainBarriers);

            // Emit per-pass memory barriers registered during the frame.
            EMemoryBarrierMask perPassMask = ActiveState.DrainMemoryBarrierForPass(passIndex);
            if (perPassMask != EMemoryBarrierMask.None)
                EmitMemoryBarrierMask(recordingState.CommandBuffer, perPassMask);

            var imageBarriers = BarrierPlanner.GetBarriersForPass(passIndex);
            var bufferBarriers = BarrierPlanner.GetBufferBarriersForPass(passIndex);
            var swapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(passIndex);

            // If the barrier planner doesn't recognise this pass at all, it has no planned
            // layout transitions. Emit a conservative full-pipeline memory barrier so that
            // all prior writes are visible to subsequent reads. We intentionally do NOT
            // substitute image barriers from another pass because those barriers carry
            // OldLayout values that may not match the images' actual layouts, causing
            // undefined behaviour (observed as CmdBlitImage segfaults on NVIDIA drivers).
            // Ops that need specific image layout transitions (e.g. blits) handle them
            // internally via TransitionForBlit.
            if (!BarrierPlanner.HasKnownPass(passIndex))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.UnknownPassBarrier.{passIndex}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Pass {0} is unknown to the barrier planner. Emitting conservative memory + image barriers.",
                    passIndex);

                MemoryBarrier safetyBarrier = new()
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit,
                    DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                };

                CmdPipelineBarrierTracked(
                    recordingState.CommandBuffer,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.AllCommandsBit,
                    DependencyFlags.None,
                    1,
                    &safetyBarrier,
                    0,
                    null,
                    0,
                    null);

                return 0;
            }

            int queueOwnershipTransfers = 0;
            int stageFlushes = 0;

            for (int i = 0; i < imageBarriers.Count; i++)
            {
                VulkanBarrierPlanner.PlannedImageBarrier planned = imageBarriers[i];
                if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                {
                    queueOwnershipTransfers++;
                }

                if (planned.Previous.StageMask != planned.Next.StageMask)
                    stageFlushes++;
            }

            for (int i = 0; i < swapchainBarriers.Count; i++)
            {
                VulkanBarrierPlanner.PlannedSwapchainBarrier planned = swapchainBarriers[i];
                if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                {
                    queueOwnershipTransfers++;
                }

                if (planned.Previous.StageMask != planned.Next.StageMask)
                    stageFlushes++;
            }

            for (int i = 0; i < bufferBarriers.Count; i++)
            {
                VulkanBarrierPlanner.PlannedBufferBarrier planned = bufferBarriers[i];
                if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                {
                    queueOwnershipTransfers++;
                }

                if (planned.Previous.StageMask != planned.Next.StageMask)
                    stageFlushes++;
            }

            if (swapchainBarriers.Count > 0 || imageBarriers.Count > 0 || bufferBarriers.Count > 0)
            {
                CmdBeginLabel(recordingState.CommandBuffer, "PassBarriers");
                EmitPlannedSwapchainBarriers(ref recordingState, recordingState.CommandBuffer, swapchainBarriers);
                EmitPlannedImageBarriers(
                    recordingState.CommandBuffer,
                    imageBarriers,
                    skipDesktopSwapchainImages: recordingState.ExcludeDesktopSwapchainBarriers);
                EmitPlannedBufferBarriers(recordingState.CommandBuffer, bufferBarriers);
                CmdEndLabel(recordingState.CommandBuffer);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBarrierPlannerPass(
                    imageBarrierCount: imageBarriers.Count + swapchainBarriers.Count,
                    bufferBarrierCount: bufferBarriers.Count,
                    queueOwnershipTransfers: queueOwnershipTransfers,
                    stageFlushes: stageFlushes);

                if (CommandRecordingDiagnosticsEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.PassBarrierSummary.{passIndex}",
                        TimeSpan.FromSeconds(2),
                        "Pass barrier summary: pass={0} image={1} buffer={2} queueTransfers={3} stageFlushes={4}",
                        passIndex,
                        imageBarriers.Count + swapchainBarriers.Count,
                        bufferBarriers.Count,
                        queueOwnershipTransfers,
                        stageFlushes);
                }
            }

            return queueOwnershipTransfers;
        }
    }
}
