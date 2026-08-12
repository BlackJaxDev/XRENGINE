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
    internal sealed partial class VulkanCommandRuntime
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

                _commandRuntime.BeginRecording(
                    VulkanApi,
                    _deviceContext.StateMachine,
                    recordingState.CommandBuffer,
                    "vkBeginCommandBuffer.Primary");

                BeginFrameTimingQueries(recordingState.CommandBuffer, recordingState.CommandBufferImageSlot);
                BeginVulkanGpuProfilerQueries(recordingState.CommandBuffer, recordingState.CommandBufferImageSlot);

                ResetCommandBufferBindState(recordingState.CommandBuffer);
                recordingState.RecordingScratch.PreparedInlineQueries.Clear();
                recordingState.RecordingScratch.BegunInlineQueries.Clear();

                if (_deviceContext.CanRecordCommandBufferDebugLabels)
                {
                    _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, recordingState.FrameDataImageIndex == recordingState.ImageIndex
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
                if (recordingState.FramePlan is not null && !recordingState.Ops.IsNumericStream)
                    throw new VulkanPlanPreconditionException(
                        "frame-plan precondition failed: sealed desktop operations were not published as a numeric stream");

                _primaryOperationScheduler.BuildSecondaryRecordingBuckets(recordingState.Ops, recordingState.SecondaryBuckets);
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
                _deviceContext.CmdBeginLabel(recordingState.CommandBuffer, "SwapchainBarriers");
                if (recordingState.SwapchainTarget.IsValid)
                {
                    VulkanBarrierPlan barrierPlan = recordingState.RenderGraphPlan.Barriers;
                    ReadOnlySpan<VulkanFrozenSwapchainBarrier> plannedSwapchainBarriers =
                        barrierPlan.GetSwapchainBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    ReadOnlySpan<VulkanFrozenImageBarrier> swapchainImageBarriers =
                        barrierPlan.GetImageBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    ReadOnlySpan<VulkanFrozenBufferBarrier> swapchainBufferBarriers =
                        barrierPlan.GetBufferBarriersForPass(VulkanBarrierPlanner.SwapchainPassIndex);
                    EmitPlannedSwapchainBarriers(ref recordingState, recordingState.CommandBuffer, plannedSwapchainBarriers);
                    EmitPlannedImageBarriers(recordingState.CommandBuffer, swapchainImageBarriers);
                    EmitPlannedBufferBarriers(recordingState.CommandBuffer, swapchainBufferBarriers);
                }
                _deviceContext.CmdEndLabel(recordingState.CommandBuffer);

                // Every physical image transition is frozen into RenderGraphPlan.Barriers.
                // Encoding must not enumerate a live resource allocator to synthesize
                // unplanned fallback barriers after prepared-input validation.
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
                recordingState.SwapchainWriterDynamicUiDrawCountByPipeline.Clear();
                recordingState.SwapchainWriterPassByPipeline.Clear();
                recordingState.SwapchainWriterOpIndexByPipeline.Clear();
                recordingState.PipelineNameByIdentity.Clear();
                recordingState.MeshDrawSlotsByRendererFamily.Clear();
                int writerCapacityHint = Math.Max(1, recordingState.RecordingScratch.RecordSwapchainWriterCapacityHint);
                recordingState.SwapchainWritesByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterLabelByPipeline.EnsureCapacity(writerCapacityHint);
                recordingState.SwapchainWriterDetailByPipeline.EnsureCapacity(writerCapacityHint);
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
                for (int opScanIndex = 0; opScanIndex < recordingState.Ops.Length; opScanIndex++)
                {
                    ref readonly FrameOperationHeader header = ref recordingState.Ops.GetHeader(opScanIndex);
                    ref readonly FrameOpContext context = ref recordingState.Ops.GetContext(opScanIndex);
                    XRFrameBuffer? target = recordingState.Ops.GetTarget(opScanIndex);
                    RememberPipelineName(ref recordingState, context);
                    switch (header.OpCode)
                    {
                        case EVulkanPrimaryPlanNodeKind.Clear:
                            ref readonly ClearPayload clear = ref recordingState.Ops.GetClear(opScanIndex);
                            recordingState.Metrics.ClearCount++;
                            if (target is null && (clear.ClearColor || clear.ClearDepth || clear.ClearStencil))
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.Metrics.SwapchainClearWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, context);
                                MarkSwapchainStaticWriter(ref recordingState, nameof(ClearOp), header.OpCode.ToString(), header.PassIndex, opScanIndex, context.PipelineIdentity);
                            }
                            break;
                        case EVulkanPrimaryPlanNodeKind.MeshDraw:
                            recordingState.Metrics.DrawCount++;
                            recordingState.Metrics.MeshDrawCount++;
                            if (target is null)
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.SwapchainDrawWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, context);
                                MarkSwapchainStaticWriter(ref recordingState, nameof(MeshDrawOp), header.OpCode.ToString(), header.PassIndex, opScanIndex, context.PipelineIdentity);
                            }
                            else
                            {
                                recordingState.Metrics.FboOnlyDrawOps++;
                            }
                            break;
                        case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                            recordingState.Metrics.DrawCount++;
                            recordingState.Metrics.IndirectDrawCount++;
                            if (target is null)
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.SwapchainDrawWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, context);
                                MarkSwapchainStaticWriter(ref recordingState, nameof(IndirectDrawOp), header.OpCode.ToString(), header.PassIndex, opScanIndex, context.PipelineIdentity);
                            }
                            else
                            {
                                recordingState.Metrics.FboOnlyDrawOps++;
                            }
                            break;
                        case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                            recordingState.Metrics.DrawCount++;
                            recordingState.Metrics.MeshTaskDispatchCount++;
                            recordingState.SwapchainWriteCount++;
                            recordingState.SwapchainDrawWrites++;
                            CountLogicalSwapchainWriter(ref recordingState, context);
                            MarkSwapchainStaticWriter(ref recordingState, nameof(MeshTaskDispatchIndirectCountOp), header.OpCode.ToString(), header.PassIndex, opScanIndex, context.PipelineIdentity);
                            break;
                        case EVulkanPrimaryPlanNodeKind.Blit:
                            ref readonly BlitPayload blit = ref recordingState.Ops.GetBlit(opScanIndex);
                            recordingState.Metrics.BlitCount++;
                            if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit))
                            {
                                recordingState.SwapchainWriteCount++;
                                recordingState.SwapchainBlitWrites++;
                                CountLogicalSwapchainWriter(ref recordingState, context);
                                MarkSwapchainStaticWriter(ref recordingState, nameof(BlitOp), header.OpCode.ToString(), header.PassIndex, opScanIndex, context.PipelineIdentity);
                            }
                            else
                            {
                                recordingState.Metrics.FboOnlyBlitOps++;
                            }
                            break;
                        case EVulkanPrimaryPlanNodeKind.ComputeDispatch or EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or EVulkanPrimaryPlanNodeKind.DlssUpscale or EVulkanPrimaryPlanNodeKind.DlssFrameGeneration: recordingState.Metrics.ComputeCount++; break;
                    }

                    if (FrameOpTraceEnabled) Debug.Vulkan("[VulkanFrameOp] index={0} op={1} pass={2} target='{3}' pipe={4} vp={5} sched={6}", opScanIndex, header.OpCode, header.PassIndex, target?.Name ?? "<swapchain>", context.PipelineIdentity, context.ViewportIdentity, context.SchedulingIdentity);
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
                    if (recordingState.Ops.GetHeader(prepareIndex).OpCode != EVulkanPrimaryPlanNodeKind.Query)
                        continue;
                    ref readonly QueryPayload pendingQuery = ref recordingState.Ops.GetQuery(prepareIndex);
                    if (pendingQuery.Operation is not (
                            ERenderQueryOperation.Reset or
                            ERenderQueryOperation.Begin or
                            ERenderQueryOperation.WriteTimestamp or
                            ERenderQueryOperation.WriteProperties) ||
                        !recordingState.RecordingScratch.PreparedInlineQueries.Add(pendingQuery.Query))
                    {
                        continue;
                    }

                    uint queryCount = 1u;
                    XRFrameBuffer? target = recordingState.Ops.GetTarget(prepareIndex);
                    if (target is not null &&
                        GenericToAPI<VkFrameBuffer>(target) is { MultiviewViewMask: not 0u } queryFbo)
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
