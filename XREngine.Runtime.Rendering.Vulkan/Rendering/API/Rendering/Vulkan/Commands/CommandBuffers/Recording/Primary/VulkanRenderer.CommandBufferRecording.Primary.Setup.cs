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

        private void ResetAndBeginPrimaryCommandBuffer(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.ResetAndBegin"))
            {
                ReleaseDeferredSecondaryCommandBuffers(recordingState.FrameDataImageIndex);
                ResetVulkanCommandBufferTracked(recordingState.CommandBuffer);
                ResetSubmissionMarkersForCommandBuffer(recordingState.CommandBuffer);
                CleanupComputeTransientResources(recordingState.FrameDataImageIndex);

                _commandRecorder.Begin(this, recordingState.CommandBuffer);

                BeginFrameTimingQueries(recordingState.CommandBuffer, recordingState.CommandBufferImageSlot);
                BeginVulkanGpuProfilerQueries(recordingState.CommandBuffer, recordingState.CommandBufferImageSlot);

                ResetCommandBufferBindState(recordingState.CommandBuffer);
                recordingState.RecordingScratch.PreparedInlineQueries.Clear();
                recordingState.RecordingScratch.BegunInlineQueries.Clear();

                if (CanRecordCommandBufferDebugLabels)
                {
                    CmdBeginLabel(recordingState.CommandBuffer, recordingState.FrameDataImageIndex == recordingState.ImageIndex
                        ? $"FrameCmd[{recordingState.ImageIndex}]"
                        : $"FrameCmd[target={recordingState.ImageIndex} frame={recordingState.FrameDataImageIndex}]");
                }
            }
        }

        private void PreparePrimaryOperationSchedule(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.SortAndSecondaryBuckets"))
            {
                if (recordingState.CommandChainSchedule is null)
                {
                    // Always sort frame ops by (PassOrder, safe draw order, OriginalIndex)
                    // and then normalize same-target clears before first same-target use.
                    // Render graph pass order preserves cross-pass dependencies, while same-pass
                    // compute/barrier/indirect operations stay in enqueue order so GPU-produced
                    // counters are written before the draw commands that consume them.
                    recordingState.Ops = _frameOperationScheduler.SortFrameOpsCore(recordingState.Ops, CompiledRenderGraph);
                }

                _frameOperationScheduler.BuildSecondaryRecordingBuckets(recordingState.Ops, recordingState.SecondaryBuckets);
                if (recordingState.SecondaryBuckets.Count > 8)
                {
                    recordingState.SecondaryBucketByStart = recordingState.RecordingScratch.SecondaryBucketByStart;
                    recordingState.SecondaryBucketByStart.Clear();
                    recordingState.SecondaryBucketByStart.EnsureCapacity(Math.Max(recordingState.RecordingScratch.SecondaryBucketByStartCapacityHint, recordingState.SecondaryBuckets.Count));
                    foreach (VulkanSecondaryRecordingBucket bucket in recordingState.SecondaryBuckets)
                        recordingState.SecondaryBucketByStart[bucket.StartIndex] = bucket;
                    recordingState.RecordingScratch.SecondaryBucketByStartCapacityHint = Math.Max(1, recordingState.SecondaryBucketByStart.Count);
                }

                if (recordingState.CommandChainSchedule is not null &&
                    TryGetCommandChainScheduleFrameSlot(recordingState.CommandChainSchedule, out int commandChainScheduleFrameSlot))
                {
                    recordingState.ScheduledCommandChainCache = GetCommandChainCache(unchecked((uint)commandChainScheduleFrameSlot));
                    if (CommandChainValidationEnabled)
                        ValidatePrimaryCommandChainSchedule(
                            recordingState.CommandChainSchedule,
                            recordingState.Ops,
                            recordingState.DynamicUiBatchTextOpCount,
                            recordingState.ScheduledCommandChainCache);
                    if (recordingState.RecordingScratch.ScheduledCommandChainKeysByOpIndex.Length < recordingState.Ops.Length)
                    {
                        int capacity = Math.Max(recordingState.Ops.Length, Math.Max(recordingState.RecordingScratch.ScheduledCommandChainKeysByOpIndex.Length * 2, 16));
                        recordingState.RecordingScratch.ScheduledCommandChainKeysByOpIndex = new CommandChainKey[capacity];
                    }
                    recordingState.ScheduledCommandChainKeysByOpIndex = recordingState.RecordingScratch.ScheduledCommandChainKeysByOpIndex;
                    PopulateCommandChainKeysByFrameOpIndex(
                        recordingState.CommandChainSchedule,
                        recordingState.ScheduledCommandChainCache,
                        recordingState.ScheduledCommandChainKeysByOpIndex.AsSpan(),
                        recordingState.Ops.Length);
                }
            }
        }

        private void EmitPrimaryFrameStartBarriers(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {

            // Ensure swapchain resources are transitioned appropriately before any rendering.
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.FrameStartBarriers"))
            {
                CmdBeginLabel(recordingState.CommandBuffer, "SwapchainBarriers");
                if (recordingState.SwapchainTarget.IsValid)
                {
                    var plannedSwapchainBarriers = BarrierPlanner.GetSwapchainBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    var swapchainImageBarriers = BarrierPlanner.GetBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    var swapchainBufferBarriers = BarrierPlanner.GetBufferBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    EmitPlannedSwapchainBarriers(ref recordingState, recordingState.CommandBuffer, plannedSwapchainBarriers);
                    EmitPlannedImageBarriers(recordingState.CommandBuffer, swapchainImageBarriers);
                    EmitPlannedBufferBarriers(recordingState.CommandBuffer, swapchainBufferBarriers);
                }
                CmdEndLabel(recordingState.CommandBuffer);

                // Transition any freshly-allocated physical images from UNDEFINED to
                // a safe initial layout so that render passes never see UNDEFINED.
                EmitInitialImageBarriersForUnknownPass(
                    recordingState.CommandBuffer,
                    skipDesktopSwapchainImages: recordingState.ExcludeDesktopSwapchainBarriers);
            }
        }

        private void ResetPrimaryRecordingScratch(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.ScratchAndUniformSlots"))
            {
                recordingState.SwapchainWritesByPipeline.Clear();
                recordingState.SwapchainWriterLabelByPipeline.Clear();
                recordingState.SwapchainWriterDetailByPipeline.Clear();
                recordingState.SwapchainWriterOpByPipeline.Clear();
                recordingState.SwapchainWriterDynamicUiDrawCountByPipeline.Clear();
                recordingState.SwapchainWriterPassByPipeline.Clear();
                recordingState.SwapchainWriterOpIndexByPipeline.Clear();
                recordingState.PipelineNameByIdentity.Clear();
                recordingState.MeshDrawSlotsByRendererFamily.Clear();
                int writerCapacityHint = Math.Max(1, recordingState.RecordingScratch.RecordSwapchainWriterCapacityHint);
                recordingState.SwapchainWritesByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterLabelByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterDetailByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterOpByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterDynamicUiDrawCountByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterPassByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterOpIndexByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.PipelineNameByIdentity.EnsureCapacity(Math.Max(1, recordingState.RecordingScratch.RecordPipelineNameCapacityHint));
                recordingState.MeshDrawSlotsByRendererFamily.EnsureCapacity(Math.Max(1, recordingState.RecordingScratch.RecordMeshDrawSlotCapacityHint));
                recordingState.MeshDrawSlotsByRendererFamily.Clear();
            }
        }

        private void CollectPrimaryOperationCensus(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.OpCensus"))
            {
                int opScanIndex = 0;
                foreach (var op in recordingState.Ops)
                {
                    switch (op)
                    {
                        case ClearOp clear:
                            RememberPipelineName(ref recordingState, clear.Context);
                            recordingState.Metrics.ClearCount++;
                            if (clear.Target is null && (clear.ClearColor || clear.ClearDepth || clear.ClearStencil))
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.Metrics.SwapchainClearWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, clear.Context);
                                MarkSwapchainFrameOpWriter(ref recordingState, nameof(ClearOp), clear, clear.PassIndex, opScanIndex, clear.Context.PipelineIdentity);
                            }
                            break;
                        case MeshDrawOp meshDraw:
                            RememberPipelineName(ref recordingState, meshDraw.Context);
                            recordingState.Metrics.DrawCount++;
                            recordingState.Metrics.MeshDrawCount++;
                            if (meshDraw.Target is null)
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.SwapchainDrawWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, meshDraw.Context);
                                MarkSwapchainFrameOpWriter(ref recordingState, nameof(MeshDrawOp), meshDraw, meshDraw.PassIndex, opScanIndex, meshDraw.Context.PipelineIdentity);
                            }
                            else
                            {
                                recordingState.Metrics.FboOnlyDrawOps++;
                            }
                            break;
                        case IndirectDrawOp indirectDraw:
                            RememberPipelineName(ref recordingState, indirectDraw.Context);
                            recordingState.Metrics.DrawCount++;
                            recordingState.Metrics.IndirectDrawCount++;
                            if (indirectDraw.Target is null)
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.SwapchainDrawWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, indirectDraw.Context);
                                MarkSwapchainFrameOpWriter(ref recordingState, nameof(IndirectDrawOp), indirectDraw, indirectDraw.PassIndex, opScanIndex, indirectDraw.Context.PipelineIdentity);
                            }
                            else
                            {
                                recordingState.Metrics.FboOnlyDrawOps++;
                            }
                            break;
                        case MeshTaskDispatchIndirectCountOp meshTaskDispatch:
                            RememberPipelineName(ref recordingState, meshTaskDispatch.Context);
                            recordingState.Metrics.DrawCount++;
                            recordingState.Metrics.MeshTaskDispatchCount++;
                            recordingState.SwapchainWriteCount++;
                            recordingState.SwapchainDrawWrites++;
                            CountLogicalSwapchainWriter(ref recordingState, meshTaskDispatch.Context);
                            MarkSwapchainFrameOpWriter(ref recordingState, nameof(MeshTaskDispatchIndirectCountOp), meshTaskDispatch, meshTaskDispatch.PassIndex, opScanIndex, meshTaskDispatch.Context.PipelineIdentity);
                            break;
                        case BlitOp blit:
                            RememberPipelineName(ref recordingState, blit.Context);
                            recordingState.Metrics.BlitCount++;
                            if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.SwapchainBlitWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, blit.Context);
                                MarkSwapchainFrameOpWriter(ref recordingState, nameof(BlitOp), blit, blit.PassIndex, opScanIndex, blit.Context.PipelineIdentity);
                            }
                            else
                            {
                                recordingState.Metrics.FboOnlyBlitOps++;
                            }
                            break;
                        case ComputeDispatchOp or ComputeDispatchIndirectOp: recordingState.Metrics.ComputeCount++; break;
                        case DlssUpscaleOp: recordingState.Metrics.ComputeCount++; break;
                        case DlssFrameGenerationOp: recordingState.Metrics.ComputeCount++; break;
                    }

                    if (FrameOpTraceEnabled)
                    {
                        Debug.Vulkan(
                            "[VulkanFrameOp] index={0} op={1} pass={2} passName='{3}' target='{4}' targetId={5} pipe={6} vp={7} sched={8}{9}",
                            opScanIndex,
                            op.GetType().Name,
                            op.PassIndex,
                            TryGetPassName(op) ?? "<unknown>",
                            ResolveCommandChainTargetName(op),
                            ResolveCommandChainTargetIdentity(op),
                            op.Context.PipelineIdentity,
                            op.Context.ViewportIdentity,
                            op.Context.SchedulingIdentity,
                            DescribeFrameOpTraceDetails(op));
                    }

                    opScanIndex++;
                }

                RecordVulkanFrameOpCensus(
                    recordingState.Ops,
                    recordingState.Metrics.ClearCount,
                    recordingState.Metrics.MeshDrawCount,
                    recordingState.Metrics.IndirectDrawCount,
                    recordingState.Metrics.MeshTaskDispatchCount,
                    recordingState.Metrics.BlitCount,
                    recordingState.Metrics.ComputeCount,
                    recordingState.SwapchainWriteCount,
                    recordingState.Metrics.FboOnlyDrawOps + recordingState.Metrics.FboOnlyBlitOps);
                if (FrameOpTraceEnabled)
                    CaptureLastFrameOpTrace(recordingState.Ops);

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.FrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] FrameOps: total={0} clears={1} draws={2} blits={3} computes={4} swapchainWrites={5} (C{6}/D{7}/B{8}) VkReq={9} VkCull={10} VkEmit={11} VkConsume={12} GpuVisible(O/M/A/E)={13}/{14}/{15}/{16}",
                        recordingState.Ops.Length,
                        recordingState.Metrics.ClearCount,
                        recordingState.Metrics.DrawCount,
                        recordingState.Metrics.BlitCount,
                        recordingState.Metrics.ComputeCount,
                        recordingState.SwapchainWriteCount,
                        recordingState.Metrics.SwapchainClearWrites,
                        recordingState.SwapchainDrawWrites,
                        recordingState.SwapchainBlitWrites,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanRequestedDraws,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanCulledDraws,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanEmittedIndirectDraws,
                        RuntimeEngine.Rendering.Stats.Vulkan.VulkanConsumedDraws,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
                        RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible);
                }

                LogSwapchainWritersByPipeline(ref recordingState, "PreOverlay");
            }
        }

        private void PreparePrimaryInlineQueries(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {

            // Reset every inline query pool before the first render operation. Query-pool
            // resets are illegal inside rendering, and deferring them until QueryOp would
            // force the forward pass through a store/reload cycle for proxy queries.
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.PrepareInlineQueries"))
            {
                for (int prepareIndex = 0; prepareIndex < recordingState.Ops.Length; prepareIndex++)
                {
                    if (recordingState.Ops[prepareIndex] is not QueryOp pendingQuery ||
                        pendingQuery.Operation is not (
                            ERenderQueryOperation.Reset or
                            ERenderQueryOperation.Begin or
                            ERenderQueryOperation.WriteTimestamp or
                            ERenderQueryOperation.WriteProperties) ||
                        !recordingState.RecordingScratch.PreparedInlineQueries.Add(pendingQuery.Query))
                    {
                        continue;
                    }

                    uint queryCount = 1u;
                    if (pendingQuery.Target is not null &&
                        GenericToAPI<VkFrameBuffer>(pendingQuery.Target) is { MultiviewViewMask: not 0u } queryFbo)
                    {
                        queryCount = (uint)System.Numerics.BitOperations.PopCount(queryFbo.MultiviewViewMask);
                    }

                    if (!pendingQuery.Query.PrepareForRecording(recordingState.CommandBuffer, queryCount))
                    {
                        recordingState.RecordingScratch.PreparedInlineQueries.Remove(pendingQuery.Query);
                    }
                }
            }
        }

    }
}
