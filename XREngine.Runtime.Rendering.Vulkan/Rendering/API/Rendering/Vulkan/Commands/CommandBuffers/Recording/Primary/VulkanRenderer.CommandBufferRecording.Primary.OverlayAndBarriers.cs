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
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {

        private unsafe void ExecuteDynamicUiBatchTextOverlay(scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            if (recordingState.DynamicUiBatchTextOpCount <= 0)
                return;

            CommandBuffer secondaryCommandBuffer = recordingState.DynamicUiBatchTextSecondaryCommandBuffer;
            if (secondaryCommandBuffer.Handle == 0)
                return;

            EndActiveRenderPass(ref recordingState);
            _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, "DynamicUIBatchText");
            TransitionSecondaryDescriptorImagesForExecution(recordingState.CommandBuffer, secondaryCommandBuffer);

            try
            {
                bool useDynamicRendering = recordingState.Policy.UseDynamicRendering &&
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
                    WriteFrozenClearValues(dynamicClearValues, 2, in recordingState.ClearState);

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

                    BeginDynamicRenderingScope(
                        recordingState.CommandBuffer,
                        in scopePlan,
                        secondaryContents: true,
                        recordingState.Policy.PreferKhrDynamicRendering);
                    CmdExecuteCommandsTracked(recordingState.CommandBuffer, 1, &secondaryCommandBuffer);
                    CmdEndDynamicRendering(
                        recordingState.CommandBuffer,
                        recordingState.Policy.PreferKhrDynamicRendering);

                    recordingState.UsedSwapchainDynamicRendering = true;
                    recordingState.SwapchainInColorAttachmentLayout = true;
                    recordingState.SwapchainClearedThisFrame = true;
                }
                else if (recordingState.SwapchainTarget.Framebuffer.Handle != 0)
                {
                    RenderPassBeginInfo renderPassInfo = new()
                    {
                        SType = StructureType.RenderPassBeginInfo,
                        RenderPass = recordingState.SwapchainTarget.LoadRenderPass,
                        Framebuffer = recordingState.SwapchainTarget.Framebuffer,
                        RenderArea = new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = recordingState.SwapchainRecordExtent
                        }
                    };

                    const uint attachmentCount = 2;
                    using VulkanNativeScratchReservation<ClearValue> clearValueReservation =
                        Synchronization._synchronizationThreadWorkspace.Current.ClearValueScratch.Reserve((int)attachmentCount);
                    Span<ClearValue> clearValues = clearValueReservation.Span;
                    fixed (ClearValue* clearValuesPtr = clearValues)
                    {
                        WriteFrozenClearValues(clearValuesPtr, attachmentCount, in recordingState.ClearState);
                        renderPassInfo.ClearValueCount = attachmentCount;
                        renderPassInfo.PClearValues = clearValuesPtr;
                        CmdBeginRenderPassTracked(recordingState.CommandBuffer, &renderPassInfo, SubpassContents.SecondaryCommandBuffers);
                        CmdExecuteCommandsTracked(recordingState.CommandBuffer, 1, &secondaryCommandBuffer);
                        Api!.CmdEndRenderPass(recordingState.CommandBuffer);
                    }

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
                _deviceContext.CmdEndLabel(recordingState.CommandBuffer);
            }
        }

        private unsafe int EmitPassBarriers(scoped ref PrimaryCommandBufferRecordingState recordingState, int passIndex)
        {
            // Emit any global pending memory barriers that accumulated before recording.
            // After the first pass consumes them they are cleared.
            EmitPendingMemoryBarriers(recordingState.CommandBuffer);

            // Emit per-pass memory barriers registered during the frame.
            EMemoryBarrierMask perPassMask = StateTracker.DrainMemoryBarrierForPass(passIndex);
            if (perPassMask != EMemoryBarrierMask.None)
                EmitMemoryBarrierMask(recordingState.CommandBuffer, perPassMask);

            VulkanBarrierPlan barrierPlan = recordingState.RenderGraphPlan.Barriers;
            ReadOnlySpan<VulkanFrozenImageBarrier> imageBarriers =
                barrierPlan.GetImageBarriersForPass(passIndex);
            ReadOnlySpan<VulkanFrozenBufferBarrier> bufferBarriers =
                barrierPlan.GetBufferBarriersForPass(passIndex);
            ReadOnlySpan<VulkanFrozenSwapchainBarrier> swapchainBarriers =
                barrierPlan.GetSwapchainBarriersForPass(passIndex);

            // An operation outside the frozen graph has no trustworthy resource or
            // stage/access contract. Do not hide the stale publication behind an
            // AllCommands barrier: abandon this command buffer and let the frame-plan
            // retry publish the exact pass generation.
            if (passIndex != VulkanBarrierPlanner.SwapchainPassIndex &&
                !recordingState.RenderGraphPlan.CompiledGraph.Plan.Execution.TryGetPassOrder(passIndex, out _))
            {
                bool contextMetadataContainsPass = TryGetPassMetadata(
                    in recordingState.ActiveContext,
                    passIndex,
                    out RenderPassMetadata contextPass);
                VulkanUnknownPassDiagnostic diagnostic = new(
                    passIndex,
                    contextMetadataContainsPass ? contextPass.Name : "<unknown>",
                    recordingState.ActiveContext.ContextKind,
                    recordingState.ActiveContext.PipelineIdentity,
                    recordingState.ActiveContext.ViewportIdentity,
                    recordingState.ActiveContext.SchedulingIdentity,
                    contextMetadataContainsPass,
                    recordingState.ActiveContext.PassMetadata?.Count ?? 0,
                    recordingState.RenderGraphPlan.Revision,
                    recordingState.RenderGraphPlan.StructuralGeneration,
                    recordingState.RenderGraphPlan.CompiledGraph.Plan.Execution.Passes.Length);
                Debug.VulkanWarningEvery(
                    "Vulkan.UnknownPassBarrier.ContextPlanMismatch",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Operation pass is unknown to the frozen barrier plan; rejecting the stale recording. {0}",
                    diagnostic);
                throw new VulkanPlanPreconditionException(
                    $"Operation pass is absent from the frozen render graph. {diagnostic}");
            }

            int queueOwnershipTransfers = 0;
            int stageFlushes = 0;

            for (int i = 0; i < imageBarriers.Length; i++)
            {
                VulkanFrozenImageBarrier planned = imageBarriers[i];
                if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                {
                    queueOwnershipTransfers++;
                }

                if (planned.Previous.StageMask != planned.Next.StageMask)
                    stageFlushes++;
            }

            for (int i = 0; i < swapchainBarriers.Length; i++)
            {
                VulkanFrozenSwapchainBarrier planned = swapchainBarriers[i];
                if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                {
                    queueOwnershipTransfers++;
                }

                if (planned.Previous.StageMask != planned.Next.StageMask)
                    stageFlushes++;
            }

            for (int i = 0; i < bufferBarriers.Length; i++)
            {
                VulkanFrozenBufferBarrier planned = bufferBarriers[i];
                if (planned.SrcQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.DstQueueFamilyIndex != Vk.QueueFamilyIgnored &&
                    planned.SrcQueueFamilyIndex != planned.DstQueueFamilyIndex)
                {
                    queueOwnershipTransfers++;
                }

                if (planned.Previous.StageMask != planned.Next.StageMask)
                    stageFlushes++;
            }

            if (!swapchainBarriers.IsEmpty || !imageBarriers.IsEmpty || !bufferBarriers.IsEmpty)
            {
                _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, "PassBarriers");
                EmitPlannedSwapchainBarriers(ref recordingState, recordingState.CommandBuffer, swapchainBarriers);
                EmitPlannedResourceBarrierBatch(
                    recordingState.CommandBuffer,
                    imageBarriers,
                    bufferBarriers,
                    recordingState.ExcludeDesktopSwapchainBarriers
                        ? recordingState.SwapchainTarget.Image
                        : default);
                _deviceContext.CmdEndLabel(recordingState.CommandBuffer);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBarrierPlannerPass(
                    imageBarrierCount: imageBarriers.Length + swapchainBarriers.Length,
                    bufferBarrierCount: bufferBarriers.Length,
                    queueOwnershipTransfers: queueOwnershipTransfers,
                    stageFlushes: stageFlushes);
                VulkanSynchronizationThreadState.BarrierExecutionTelemetry telemetry =
                    Synchronization._synchronizationThreadWorkspace.Current.GetBarrierExecutionTelemetry(
                        recordingState.RenderGraphPlan.CompiledGraph.Plan.Execution.EdgeCount);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGraphBarrierExecution(
                    telemetry.Reservations,
                    telemetry.RequestedBytes,
                    telemetry.HighWaterBytes,
                    telemetry.GraphEdgeCount);

                if (CommandRecordingDiagnosticsEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.PassBarrierSummary.{passIndex}",
                        TimeSpan.FromSeconds(2),
                        "Pass barrier summary: pass={0} image={1} buffer={2} queueTransfers={3} stageFlushes={4}",
                        passIndex,
                        imageBarriers.Length + swapchainBarriers.Length,
                        bufferBarriers.Length,
                        queueOwnershipTransfers,
                        stageFlushes);
                }
            }

            return queueOwnershipTransfers;
        }
    }
}
