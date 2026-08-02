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

        private void RecordPrimaryOperations(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.MainOpLoop"))
            {
                for (int opIndex = 0; opIndex < recordingState.Ops.Length; opIndex++)
                {
                    if (recordingState.PipelineDeferredOps.Contains(recordingState.Ops[opIndex]))
                        continue;

                    ref readonly VulkanPrimaryPlanNode primaryNode =
                        ref recordingState.PrimaryCommandPlan.GetNode(opIndex);
                    FrameOp op = primaryNode.Operation
                        ?? throw new InvalidOperationException(
                            "A terminal primary-plan node appeared in the frame-operation range.");
                    try
                    {
                        if (primaryNode.Kind ==
                            EVulkanPrimaryPlanNodeKind.TextureUpload)
                        {
                            TextureUploadFrameOp textureUploadOp =
                                (TextureUploadFrameOp)op;
                            if ((primaryNode.Actions &
                                EVulkanPrimaryPlanAction.EndRendering) != 0)
                                EndActiveRenderPass(ref recordingState);
                            if (recordingState.PassIndexLabelActive)
                            {
                                CmdEndLabel(recordingState.CommandBuffer);
                                recordingState.PassIndexLabelActive = false;
                            }

                            CmdBeginLabel(recordingState.CommandBuffer, "TextureUpload");
                            RecordVulkanCommandDiagnosticMarker(recordingState.CommandBuffer, textureUploadOp, textureUploadOp.PassIndex, opIndex);
                            RecordTextureUploadOp(recordingState.CommandBuffer, textureUploadOp.Upload);
                            CmdEndLabel(recordingState.CommandBuffer);
                            continue;
                        }

                        if (!recordingState.HasActiveContext || !FrameOpContextCompatibility.AreRecordingCompatible(recordingState.ActiveContext, op.Context))
                        {
                            IDisposable? contextChangeProfileScope = null;
                            if (CommandRecordingDetailProfilingEnabled)
                                contextChangeProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.ContextChange");
                            try
                            {
                                // When the context changes but both the active render pass and the
                                // incoming op target the swapchain (target == null), keep the render
                                // pass alive.  Ending and re-beginning the swapchain render pass
                                // causes a storeOp â†’ layout transition â†’ loadOp cycle that can lose
                                // composited content (e.g. the skybox turns black).
                                int incomingPassIndex = op.PassIndex;

                                // Query begin/draw/end capture their contexts independently.
                                // Descriptor or resource generations may advance while the
                                // enclosed mesh is prepared, but that must not split an otherwise
                                // compatible Vulkan rendering scope and leave an empty query.
                                bool preservedRenderPass = recordingState.RenderScope.ShouldPreserveForContextChange(
                                    VulkanSwapchainContextCoalescer.TargetsSwapchain(op),
                                    op.Target,
                                    incomingPassIndex,
                                    recordingState.ActiveInlineQuery is not null,
                                    op.Context.SchedulingIdentity,
                                    recordingState.ActivePassIndex,
                                    recordingState.ActiveSchedulingIdentity,
                                    FrameOpContextCompatibility.AreQueryScopeCompatible(recordingState.ActiveContext, op.Context));

                                if (!preservedRenderPass)
                                {
                                    EndActiveRenderPass(ref recordingState);
                                }

                                if (!preservedRenderPass && recordingState.PassIndexLabelActive)
                                {
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    recordingState.PassIndexLabelActive = false;
                                }

                                recordingState.ActiveContext = op.Context;
                                recordingState.HasActiveContext = true;
                                ApplyPipelineOverride(ref recordingState, recordingState.ActiveContext);

                                if (TryActivateFrameOpResourcePlannerState(recordingState.ActiveContext))
                                {
                                    recordingState.PlannerContext = recordingState.ActiveContext;
                                    recordingState.HasPlannerContext = true;
                                }
                                else if (recordingState.ActiveContext.PipelineInstance is not null && !recordingState.HasPlannerContext)
                                {
                                    recordingState.PlannerContext = recordingState.ActiveContext;
                                    recordingState.HasPlannerContext = true;
                                }
                                else if (recordingState.ActiveContext.PipelineInstance is not null &&
                                    RequiresResourcePlannerRebuild(recordingState.PlannerContext, recordingState.ActiveContext))
                                {
                                    Debug.VulkanWarningEvery(
                                        $"Vulkan.ResourcePlanner.ContextChangeDuringRecord.{recordingState.ActiveContext.PipelineIdentity}.{recordingState.ActiveContext.ViewportIdentity}",
                                        TimeSpan.FromSeconds(2),
                                        "[VulkanResourcePlanner] Keeping pre-recorded physical plan during command-buffer recording despite context change. OldPipe={0} NewPipe={1} OldVp={2} NewVp={3}.",
                                        recordingState.PlannerContext.PipelineIdentity,
                                        recordingState.ActiveContext.PipelineIdentity,
                                        recordingState.PlannerContext.ViewportIdentity,
                                        recordingState.ActiveContext.ViewportIdentity);
                                }

                                if (preservedRenderPass)
                                {
                                    recordingState.ActiveSchedulingIdentity = op.Context.SchedulingIdentity;
                                }
                                else
                                {
                                    recordingState.ActivePassIndex = int.MinValue;
                                    recordingState.ActiveSchedulingIdentity = int.MinValue;
                                }
                            }
                            finally
                            {
                                contextChangeProfileScope?.Dispose();
                            }
                        }

                        int opPassIndex = op.PassIndex;

                        if (opPassIndex == int.MinValue)
                        {
                            recordingState.Metrics.DroppedFrameOps++;
                            if (op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                                recordingState.Metrics.DroppedDrawOps++;
                            if (op is ComputeDispatchOp)
                                recordingState.Metrics.DroppedComputeOps++;
                            recordingState.Metrics.FirstFailure ??= CaptureFrameOpFailure(op, new InvalidOperationException("No valid render-graph pass index could be resolved."));

                            Debug.VulkanWarningEvery(
                                $"Vulkan.OpDroppedNoPass.{op.GetType().Name}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Dropping op '{0}' because no valid render-graph pass index could be resolved.",
                                op.GetType().Name);
                            continue;
                        }

                        if (recordingState.SkipUiPipelineOps && op.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
                        {
                            recordingState.Metrics.DroppedFrameOps++;
                            if (op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                                recordingState.Metrics.DroppedDrawOps++;
                            if (op is ComputeDispatchOp)
                                recordingState.Metrics.DroppedComputeOps++;

                            Debug.VulkanEvery(
                                $"Vulkan.SkipUiPipeline.{GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Skipping UI pipeline op {0} pass={1} pipe={2} due to XRE_SKIP_UI_PIPELINE=1.",
                                op.GetType().Name,
                                opPassIndex,
                                op.Context.PipelineIdentity);
                            continue;
                        }

                        if (recordingState.SkipUiBatchTextOps && IsUiBatchTextDrawOp(op))
                        {
                            recordingState.Metrics.DroppedFrameOps++;
                            recordingState.Metrics.DroppedDrawOps++;

                            Debug.VulkanEvery(
                                $"Vulkan.SkipUiBatchText.{GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Skipping batched UI text op pass={0} pipe={1} due to XRE_SKIP_UI_BATCH_TEXT=1.",
                                opPassIndex,
                                op.Context.PipelineIdentity);
                            continue;
                        }

                        // Diagnostic: log the first few ops with invalid pass index per frame
                        if (op.PassIndex == int.MinValue)
                        {
                            Debug.VulkanWarningEvery(
                                $"Vulkan.OpInvalidPass.{op.GetType().Name}",
                                TimeSpan.FromSeconds(2),
                                "[Vulkan] Op[{0}] {1} had PassIndex=MinValue (resolved to {2}). " +
                                "CtxPipeline={3} CtxMetadataCount={4} CtxViewport={5}",
                                opIndex,
                                op.GetType().Name,
                                opPassIndex,
                                op.Context.PipelineIdentity,
                                op.Context.PassMetadata?.Count ?? -1,
                                op.Context.ViewportIdentity);
                        }

                        int opSchedulingIdentity = op.Context.SchedulingIdentity;
                        if ((primaryNode.Actions &
                                EVulkanPrimaryPlanAction.BarrierBatch) != 0 &&
                            (opPassIndex != recordingState.ActivePassIndex ||
                             opSchedulingIdentity != recordingState.ActiveSchedulingIdentity))
                        {
                            IDisposable? passTransitionProfileScope = null;
                            if (CommandRecordingDetailProfilingEnabled)
                                passTransitionProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.PassTransition");
                            try
                            {
                                using VulkanCpuStageScope transitionStage =
                                    new(EVulkanCpuStage.ContextPassTransitions);
                                // Barriers are safest outside render passes.
                                EndActiveRenderPass(ref recordingState);

                                if (recordingState.PassIndexLabelActive)
                                {
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    recordingState.PassIndexLabelActive = false;
                                }

                                if (CanRecordCommandBufferDebugLabels)
                                {
                                    recordingState.PassIndexLabelActive = CmdBeginLabel(
                                        recordingState.CommandBuffer,
                                        $"Pass={opPassIndex} Pipe={op.Context.PipelineIdentity} Vp={op.Context.ViewportIdentity}");
                                }

                                using (VulkanCpuStageScope barrierStage =
                                    new(EVulkanCpuStage.BarrierPlanningEmission))
                                {
                                    int emittedQueueOwnershipTransfers =
                                        EmitPassBarriers(ref recordingState, opPassIndex);
                                    bool plannedQueueOwnershipTransfer =
                                        (primaryNode.Actions &
                                         EVulkanPrimaryPlanAction.QueueOwnershipTransfer) != 0;
                                    if (plannedQueueOwnershipTransfer !=
                                        (emittedQueueOwnershipTransfers > 0))
                                    {
                                        throw new InvalidOperationException(
                                            $"Primary plan queue-ownership action mismatch for pass {opPassIndex}: " +
                                            $"planned={plannedQueueOwnershipTransfer} emitted={emittedQueueOwnershipTransfers}.");
                                    }
                                }
                                TransitionFrameOpDescriptorSnapshotsForSampling(
                                    recordingState.CommandBuffer,
                                    recordingState.Ops,
                                    opIndex,
                                    opPassIndex,
                                    opSchedulingIdentity,
                                    recordingState.MeshDrawUniformSlotsByOpIndex,
                                    recordingState.MeshDrawSlotsByRendererFamily,
                                    recordingState.MeshFrameDataFamilyBases,
                                    recordingState.CommandBufferImageSlot);
                                recordingState.ActivePassIndex = opPassIndex;
                                recordingState.ActiveSchedulingIdentity = opSchedulingIdentity;
                            }
                            finally
                            {
                                passTransitionProfileScope?.Dispose();
                            }
                        }

                        RecordVulkanCommandDiagnosticMarker(recordingState.CommandBuffer, op, opPassIndex, opIndex);
                        using var vulkanGpuScope = TryBeginVulkanGpuProfilerScope(recordingState.CommandBuffer, op, opPassIndex);

                        IDisposable? frameOpProfileScope = null;
                        if (CommandRecordingDetailProfilingEnabled)
                            frameOpProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope(GetRecordPrimaryFrameOpProfileScopeName(op));
                        try
                        {
                            using VulkanCpuStageScope opDispatchStage =
                                new(EVulkanCpuStage.OpDispatch);
                            System.Diagnostics.Debug.Assert(
                                (primaryNode.Actions &
                                    EVulkanPrimaryPlanAction.RecordOperation) != 0,
                                "Every semantic primary-plan node must publish an operation-record action.");
                            bool primaryNodeBeginsRendering =
                                (primaryNode.Actions &
                                    EVulkanPrimaryPlanAction.BeginRendering) != 0;
                            if ((primaryNode.Actions &
                                EVulkanPrimaryPlanAction.EndRendering) != 0)
                                EndActiveRenderPass(ref recordingState);
                            switch (primaryNode.Kind)
                            {
                                case EVulkanPrimaryPlanNodeKind.Blit:
                                    BlitOp blit = (BlitOp)op;
                                    if (blit.ColorBit && (blit.InFbo is null || blit.OutFbo is null))
                                        EnsureSwapchainColorAttachmentLayoutForBlit(ref recordingState);
                                    CmdBeginLabel(recordingState.CommandBuffer, "Blit");
                                    bool blitRecorded = RecordBlitOp(recordingState.CommandBuffer, recordingState.ImageIndex, blit, in recordingState.SwapchainTarget);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    if (blit.OutFbo is null && (blit.ColorBit || blit.DepthBit || blit.StencilBit) && blitRecorded)
                                    {
                                        recordingState.SwapchainWrittenOutsideRenderPass = true;
                                        if (blit.ColorBit)
                                        {
                                            recordingState.SwapchainInColorAttachmentLayout = true;
                                            recordingState.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal;
                                        }
                                        recordingState.ActualSwapchainWriteCount++;
                                    }
                                    break;

                                case EVulkanPrimaryPlanNodeKind.Clear:
                                    ClearOp clear = (ClearOp)op;
                                    if (CommandRecordingDiagnosticsEnabled && clear.Target?.Name == "ForwardPassFBO")
                                    {
                                        Debug.VulkanEvery(
                                            "Vulkan.FwdClear",
                                            TimeSpan.FromSeconds(2),
                                            "[Vulkan][FwdClear] ForwardPassFBO clear pass={0} color={1} depth={2} stencil={3}",
                                            opPassIndex, clear.ClearColor, clear.ClearDepth, clear.ClearStencil);
                                    }
                                    if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(clear.Target?.Name))
                                    {
                                        Debug.VulkanEvery(
                                            $"DeferredLighting.ClearOp.{clear.Target?.Name}",
                                            TimeSpan.FromSeconds(1),
                                            "[DeferredLightingDiag][ClearOp] target='{0}' pass={1} color={2} depth={3} stencil={4} renderScope.Target='{5}'",
                                            clear.Target?.Name ?? "<swapchain>",
                                            opPassIndex,
                                            clear.ClearColor,
                                            clear.ClearDepth,
                                            clear.ClearStencil,
                                            recordingState.RenderScope.Target?.Name ?? "<none>");
                                    }

                                    System.Diagnostics.Debug.Assert(
                                        primaryNodeBeginsRendering,
                                        "Clear primary-plan nodes must own render-scope entry.");
                                    if (primaryNodeBeginsRendering &&
                                        (!recordingState.RenderScope.IsActive || recordingState.RenderScope.Target != clear.Target))
                                    {
                                        EndActiveRenderPass(ref recordingState);
                                        BeginRenderPassForTarget(ref recordingState, clear.Target, opPassIndex, recordingState.ActiveContext);
                                    }

                                    // Skip explicit color clears on the swapchain after the first render pass.
                                    // CmdClearAttachments would erase scene content composited by an earlier pipeline.
                                    // Depth/stencil clears are still allowed since they don't affect composited color.
                                    bool clearRecorded = false;
                                    uint clearRenderLayerCount = recordingState.RenderScope.UsesDynamicRendering
                                        ? Math.Max(recordingState.RenderScope.DynamicRenderingFormats.LayerCount, 1u)
                                        : 0u;
                                    uint clearRenderViewMask = recordingState.RenderScope.UsesDynamicRendering
                                        ? recordingState.RenderScope.DynamicRenderingFormats.ViewMask
                                        : 0u;
                                    if (clear.Target is null && recordingState.SwapchainClearedThisFrame && clear.ClearColor)
                                    {
                                        if (clear.ClearDepth || clear.ClearStencil)
                                        {
                                            // Emit depth/stencil clear only â€” strip the color clear.
                                            RecordClearOp(
                                                recordingState.CommandBuffer,
                                                recordingState.ImageIndex,
                                                clear,
                                                recordingState.RenderScope.RenderArea,
                                                in recordingState.SwapchainTarget,
                                                clearRenderLayerCount,
                                                clearRenderViewMask,
                                                suppressColorClear: true);
                                            clearRecorded = true;
                                        }
                                        // else: pure color clear on swapchain after first pass â†’ skip entirely
                                    }
                                    else
                                    {
                                        RecordClearOp(recordingState.CommandBuffer, recordingState.ImageIndex, clear, recordingState.RenderScope.RenderArea, in recordingState.SwapchainTarget, clearRenderLayerCount, clearRenderViewMask);
                                        clearRecorded = true;
                                    }
                                    if (clear.Target is null && clearRecorded)
                                        recordingState.ActualSwapchainWriteCount++;
                                    break;

                                case EVulkanPrimaryPlanNodeKind.TransformFeedback:
                                    TransformFeedbackOp transformFeedbackOp =
                                        (TransformFeedbackOp)op;
                                    System.Diagnostics.Debug.Assert(
                                        primaryNodeBeginsRendering,
                                        "Transform-feedback primary-plan nodes must own render-scope entry.");
                                    if (primaryNodeBeginsRendering &&
                                        (!recordingState.RenderScope.IsActive || recordingState.RenderScope.Target != transformFeedbackOp.Target))
                                    {
                                        EndActiveRenderPass(ref recordingState);
                                        BeginRenderPassForTarget(ref recordingState, transformFeedbackOp.Target, opPassIndex, recordingState.ActiveContext);
                                    }

                                    bool transformFeedbackLabelActive = false;
                                    if (CanRecordCommandBufferDebugLabels)
                                        transformFeedbackLabelActive = CmdBeginLabel(recordingState.CommandBuffer, $"TransformFeedback.{transformFeedbackOp.Operation}");
                                    RecordTransformFeedbackOp(recordingState.CommandBuffer, transformFeedbackOp);
                                    if (transformFeedbackLabelActive)
                                        CmdEndLabel(recordingState.CommandBuffer);
                                    break;

                                case EVulkanPrimaryPlanNodeKind.Query:
                                    QueryOp queryOp = (QueryOp)op;
                                    if ((primaryNode.Actions &
                                            EVulkanPrimaryPlanAction
                                                .ExecuteSecondaryRange) != 0 &&
                                        TryGetSecondaryBucketForStart(
                                            recordingState.SecondaryBuckets,
                                            recordingState.SecondaryBucketByStart,
                                            opIndex,
                                            out
                                            VulkanSecondaryRecordingBucket
                                                queryBucket) &&
                                        TryRecordSecondaryBucket(
                                            primaryCommandBuffer:
                                                recordingState.CommandBuffer,
                                            recordingState.FrameDataImageIndex,
                                            recordingState.ExecutedCommandChainSecondaryHandles,
                                            recordingState.Ops,
                                            opIndex,
                                            queryBucket,
                                            opPassIndex,
                                            recordingState.RenderScope.IsActive,
                                            recordingState.ActiveInlineQuery is not null,
                                            $"Query.{queryOp.Operation}"))
                                    {
                                        opIndex =
                                            opIndex +
                                            queryBucket.Count -
                                            1;
                                        break;
                                    }

                                    if (queryOp.Operation == ERenderQueryOperation.Reset)
                                        break;

                                    if (queryOp.Operation == ERenderQueryOperation.WriteTimestamp)
                                    {
                                        if (recordingState.RecordingScratch.PreparedInlineQueries.Contains(queryOp.Query) &&
                                            queryOp.Query.WriteTimestamp(
                                                recordingState.CommandBuffer,
                                                queryOp.TimestampStage,
                                                queryOp.PointIndex) != ERenderQueryReadStatus.Ready)
                                        {
                                            recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                        }
                                        break;
                                    }

                                    if (queryOp.Operation == ERenderQueryOperation.WriteProperties)
                                    {
                                        if (!recordingState.RecordingScratch.PreparedInlineQueries.Contains(queryOp.Query) ||
                                            queryOp.Query.WriteProperties(
                                                recordingState.CommandBuffer,
                                                queryOp.SourceHandles.Span) != ERenderQueryReadStatus.Ready)
                                        {
                                            recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                        }
                                        break;
                                    }

                                    if (queryOp.Operation == ERenderQueryOperation.CopyResults)
                                    {
                                        if (queryOp.Query.CopyResults(
                                                recordingState.CommandBuffer,
                                                queryOp.ResultDestination,
                                                queryOp.ResultDestinationOffset,
                                                queryOp.ResultStride,
                                                queryOp.IncludeAvailability) != ERenderQueryReadStatus.Ready)
                                        {
                                            recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                        }
                                        break;
                                    }

                                    bool firstBeginForQuery = queryOp.Operation == ERenderQueryOperation.Begin &&
                                        !recordingState.RecordingScratch.BegunInlineQueries.Contains(queryOp.Query);
                                    if (firstBeginForQuery &&
                                        !recordingState.RecordingScratch.PreparedInlineQueries.Contains(queryOp.Query))
                                    {
                                        recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                        Debug.VulkanWarningEvery(
                                            $"Vulkan.UnpreparedInlineOcclusionQuery.{queryOp.Query.GetHashCode()}",
                                            TimeSpan.FromSeconds(1),
                                            "[Vulkan] Inline occlusion query begin suppressed because its pool was not prepared. Query='{0}' pass={1} op={2}.",
                                            queryOp.Query.Data.Name ?? "<unnamed>",
                                            opPassIndex,
                                            opIndex);
                                    }

                                    System.Diagnostics.Debug.Assert(
                                        primaryNodeBeginsRendering,
                                        "Inline query begin/end primary-plan nodes must own render-scope entry.");
                                    if (primaryNodeBeginsRendering &&
                                        (!recordingState.RenderScope.IsActive || recordingState.RenderScope.Target != queryOp.Target))
                                    {
                                        EndActiveRenderPass(ref recordingState);
                                        BeginRenderPassForTarget(ref recordingState, queryOp.Target, opPassIndex, recordingState.ActiveContext);
                                    }

                                    bool queryLabelActive = false;
                                    if (CanRecordCommandBufferDebugLabels)
                                        queryLabelActive = CmdBeginLabel(recordingState.CommandBuffer, $"Query.{queryOp.Operation}");
                                    if (queryOp.Operation == ERenderQueryOperation.Begin)
                                    {
                                        if (recordingState.ActiveInlineQuery is not null)
                                        {
                                            recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                            queryOp.Query.InvalidateRecordedResultEpoch(recordingState.CommandBuffer);
                                            Debug.VulkanWarningEvery(
                                                $"Vulkan.NestedInlineQuery.{queryOp.Query.GetHashCode()}",
                                                TimeSpan.FromSeconds(1),
                                                "[Vulkan.Query] Nested query begin rejected. active='{0}' requested='{1}' pass={2} op={3}.",
                                                recordingState.ActiveInlineQuery.Data.Name ?? recordingState.ActiveInlineQuery.Data.Descriptor.Kind.ToString(),
                                                queryOp.Query.Data.Name ?? queryOp.Descriptor.Kind.ToString(),
                                                opPassIndex,
                                                opIndex);
                                        }
                                        else if (recordingState.RecordingScratch.PreparedInlineQueries.Contains(queryOp.Query) &&
                                            recordingState.RecordingScratch.BegunInlineQueries.Add(queryOp.Query))
                                        {
                                            recordingState.ActiveInlineQuery = queryOp.Query.BeginQuery(recordingState.CommandBuffer) == ERenderQueryReadStatus.Ready
                                                ? queryOp.Query
                                                : null;
                                            if (recordingState.ActiveInlineQuery is null)
                                                recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                            recordingState.ActiveInlineQueryRecordedDraw = false;
                                        }
                                        else if (recordingState.RecordingScratch.PreparedInlineQueries.Contains(queryOp.Query))
                                        {
                                            recordingState.ActiveInlineQuery = null;
                                            recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                            Debug.VulkanWarningEvery(
                                                $"Vulkan.DuplicateInlineOcclusionQuery.{queryOp.Query.GetHashCode()}",
                                                TimeSpan.FromSeconds(1),
                                                "[Vulkan] Duplicate inline occlusion query begin suppressed in one command buffer. Query='{0}' pass={1} op={2}.",
                                                queryOp.Query.Data.Name ?? "<unnamed>",
                                                opPassIndex,
                                                opIndex);
                                        }
                                    }
                                    else if (ReferenceEquals(recordingState.ActiveInlineQuery, queryOp.Query))
                                    {
                                        if (!recordingState.ActiveInlineQueryRecordedDraw)
                                        {
                                            recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                            recordingState.ActiveInlineQuery.InvalidateRecordedResultEpoch(recordingState.CommandBuffer);
                                            Debug.VulkanWarningEvery(
                                                $"Vulkan.EmptyInlineQuery.{recordingState.ActiveInlineQuery.GetHashCode()}",
                                                TimeSpan.FromSeconds(1),
                                                "[Vulkan] Inline occlusion query contained no recorded draw; this epoch will resolve visible. Query='{0}'.",
                                                recordingState.ActiveInlineQuery.Data.Name ?? "<unnamed>");
                                        }
                                        queryOp.Query.EndQuery(recordingState.CommandBuffer);
                                        recordingState.ActiveInlineQuery = null;
                                        recordingState.ActiveInlineQueryRecordedDraw = false;
                                    }
                                    else
                                    {
                                        recordingState.QueryFrameOpsRequireRerecordLocal = true;
                                        queryOp.Query.InvalidateRecordedResultEpoch(recordingState.CommandBuffer);
                                        Debug.VulkanWarningEvery(
                                            $"Vulkan.MismatchedInlineQueryEnd.{queryOp.Query.GetHashCode()}",
                                            TimeSpan.FromSeconds(1),
                                            "[Vulkan.Query] Query end rejected because it does not match the active query. active='{0}' requested='{1}' pass={2} op={3}.",
                                            recordingState.ActiveInlineQuery?.Data.Name ?? "<none>",
                                            queryOp.Query.Data.Name ?? queryOp.Descriptor.Kind.ToString(),
                                            opPassIndex,
                                            opIndex);
                                    }
                                    if (queryLabelActive)
                                        CmdEndLabel(recordingState.CommandBuffer);
                                    break;

                                case EVulkanPrimaryPlanNodeKind.MeshDraw:
                                    MeshDrawOp drawOp = (MeshDrawOp)op;
                                    if (CommandRecordingDiagnosticsEnabled &&
                                        string.Equals(
                                            drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name,
                                            "CpuOcclusionProxy.UnitCube",
                                            StringComparison.Ordinal))
                                    {
                                        Debug.VulkanEvery(
                                            "Vulkan.CpuOcclusionProxy.RecordState",
                                            TimeSpan.FromSeconds(1),
                                            "[Vulkan][CpuQueryDiag] activeQuery={0} viewport=({1},{2},{3},{4}) scissor=({5},{6},{7},{8}) modelT=({9:F3},{10:F3},{11:F3}) modelS=({12:F3},{13:F3},{14:F3}) cameraT=({15:F3},{16:F3},{17:F3}).",
                                            recordingState.ActiveInlineQuery is not null,
                                            drawOp.Draw.Viewport.X,
                                            drawOp.Draw.Viewport.Y,
                                            drawOp.Draw.Viewport.Width,
                                            drawOp.Draw.Viewport.Height,
                                            drawOp.Draw.Scissor.Offset.X,
                                            drawOp.Draw.Scissor.Offset.Y,
                                            drawOp.Draw.Scissor.Extent.Width,
                                            drawOp.Draw.Scissor.Extent.Height,
                                            drawOp.Draw.ModelMatrix.M41,
                                            drawOp.Draw.ModelMatrix.M42,
                                            drawOp.Draw.ModelMatrix.M43,
                                            drawOp.Draw.ModelMatrix.M11,
                                            drawOp.Draw.ModelMatrix.M22,
                                            drawOp.Draw.ModelMatrix.M33,
                                            drawOp.Draw.CameraPosition.X,
                                            drawOp.Draw.CameraPosition.Y,
                                            drawOp.Draw.CameraPosition.Z);
                                    }

                                    int meshCommandChainRunCount = CountContiguousMeshCommandChainRun(ref recordingState, opIndex, drawOp, opPassIndex);
                                    if ((primaryNode.Actions &
                                            EVulkanPrimaryPlanAction.ExecuteSecondaryRange) != 0 &&
                                        (TryExecuteScheduledMeshCommandChainSecondaryRun(ref recordingState, opIndex, meshCommandChainRunCount, opPassIndex, drawOp) ||
                                         TryExecuteMeshCommandChainSecondaryRun(ref recordingState, opIndex, meshCommandChainRunCount, opPassIndex, drawOp)))
                                    {
                                        if (drawOp.Target is null)
                                            recordingState.ActualSwapchainWriteCount += meshCommandChainRunCount;
                                        opIndex = opIndex + meshCommandChainRunCount - 1;
                                        break;
                                    }

                                    int inlineDrawUniformSlot = GetMeshDrawUniformSlot(ref recordingState,
                                        opIndex,
                                        drawOp.Draw.Renderer,
                                        drawOp.Context,
                                        drawOp.Draw);
                                    System.Diagnostics.Debug.Assert(
                                        primaryNodeBeginsRendering,
                                        "Mesh-draw primary-plan nodes must own render-scope entry.");
                                    if (primaryNodeBeginsRendering &&
                                        !recordingState.RenderScope.MatchesTarget(drawOp.Target))
                                    {
                                        EndActiveRenderPass(ref recordingState);
                                        using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(drawOp.Context);
                                        drawOp.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                                            recordingState.CommandBuffer,
                                            drawOp.Draw,
                                            inlineDrawUniformSlot,
                                            recordingState.CommandBufferImageSlot,
                                            drawOp.Target);
                                        BeginRenderPassForTarget(ref recordingState, drawOp.Target, opPassIndex, recordingState.ActiveContext);
                                    }

                                    bool recordedInlineDraw = RecordMeshDrawIntoCommandBuffer(ref recordingState,
                                        recordingState.CommandBuffer,
                                        drawOp,
                                        opPassIndex,
                                        inlineDrawUniformSlot);
                                    if (recordingState.ActiveInlineQuery is not null && recordedInlineDraw)
                                        recordingState.ActiveInlineQueryRecordedDraw = true;
                                    if (drawOp.Target is null)
                                        recordingState.ActualSwapchainWriteCount++;
                                    break;

                                case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                                    IndirectDrawOp indirectOp =
                                        (IndirectDrawOp)op;
                                    int indirectCommandChainRunCount = CountContiguousIndirectCommandChainRun(ref recordingState, opIndex, indirectOp, opPassIndex);
                                    if ((primaryNode.Actions &
                                            EVulkanPrimaryPlanAction.ExecuteSecondaryRange) != 0 &&
                                        TryExecuteIndirectCommandChainSecondaryRun(ref recordingState, opIndex, indirectCommandChainRunCount, opPassIndex, indirectOp))
                                    {
                                        if (indirectOp.Target is null)
                                            recordingState.ActualSwapchainWriteCount += indirectCommandChainRunCount;
                                        opIndex = opIndex + indirectCommandChainRunCount - 1;
                                        break;
                                    }

                                    EmitIndirectDrawRunReadBarrier(ref recordingState);
                                    System.Diagnostics.Debug.Assert(
                                        primaryNodeBeginsRendering,
                                        "Indirect-draw primary-plan nodes must own render-scope entry.");
                                    if (primaryNodeBeginsRendering)
                                        BeginRenderPassForTarget(ref recordingState, indirectOp.Target, opPassIndex, recordingState.ActiveContext);

                                    CmdBeginLabel(recordingState.CommandBuffer, "IndirectDraw");
                                    RecordIndirectDrawIntoCommandBuffer(ref recordingState, recordingState.CommandBuffer, indirectOp, opPassIndex, opIndex);
                                    CmdEndLabel(recordingState.CommandBuffer);

                                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(
                                        usedSecondary: false,
                                        usedParallel: false,
                                        opCount: 1);
                                    if (indirectOp.Target is null)
                                        recordingState.ActualSwapchainWriteCount++;
                                    break;

                                case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                                    MeshTaskDispatchIndirectCountOp meshTaskOp =
                                        (MeshTaskDispatchIndirectCountOp)op;
                                    System.Diagnostics.Debug.Assert(
                                        primaryNodeBeginsRendering,
                                        "Mesh-task primary-plan nodes must own render-scope entry.");
                                    if (primaryNodeBeginsRendering &&
                                        !recordingState.RenderScope.MatchesTarget(null))
                                    {
                                        EndActiveRenderPass(ref recordingState);
                                        BeginRenderPassForTarget(ref recordingState, null, opPassIndex, recordingState.ActiveContext);
                                    }

                                    CmdBeginLabel(recordingState.CommandBuffer, "MeshTaskDispatchIndirectCount");
                                    RecordMeshTaskDispatchIndirectCountOp(recordingState.CommandBuffer, meshTaskOp);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    recordingState.ActualSwapchainWriteCount++;
                                    break;

                                case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
                                    ComputeDispatchOp computeOp =
                                        (ComputeDispatchOp)op;
                                    if ((primaryNode.Actions &
                                            EVulkanPrimaryPlanAction.ExecuteSecondaryRange) != 0 &&
                                        TryGetSecondaryBucketForStart(recordingState.SecondaryBuckets, recordingState.SecondaryBucketByStart, opIndex, out VulkanSecondaryRecordingBucket computeBucket) &&
                                        TryRecordSecondaryBucket(
                                            primaryCommandBuffer: recordingState.CommandBuffer,
                                            recordingState.FrameDataImageIndex,
                                            recordingState.ExecutedCommandChainSecondaryHandles,
                                            recordingState.Ops,
                                            opIndex,
                                            computeBucket,
                                            opPassIndex,
                                            recordingState.RenderScope.IsActive,
                                            recordingState.ActiveInlineQuery is not null,
                                            "ComputeDispatch"))
                                    {
                                        opIndex = opIndex + computeBucket.Count - 1;
                                    }
                                    else
                                    {
                                        CmdBeginLabel(recordingState.CommandBuffer, "ComputeDispatch");
                                        RecordComputeDispatchOp(recordingState.CommandBuffer, recordingState.FrameDataImageIndex, computeOp, opIndex);
                                        CmdEndLabel(recordingState.CommandBuffer);
                                    }
                                    break;

                                case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect:
                                    ComputeDispatchIndirectOp computeIndirectOp =
                                        (ComputeDispatchIndirectOp)op;
                                    CmdBeginLabel(recordingState.CommandBuffer, computeIndirectOp.Label);
                                    RecordComputeDispatchIndirectOp(recordingState.CommandBuffer, recordingState.FrameDataImageIndex, computeIndirectOp);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    break;

                                case EVulkanPrimaryPlanNodeKind.BufferCopy:
                                    BufferCopyOp bufferCopyOp =
                                        (BufferCopyOp)op;
                                    if ((primaryNode.Actions &
                                            EVulkanPrimaryPlanAction.ExecuteSecondaryRange) != 0 &&
                                        TryGetSecondaryBucketForStart(recordingState.SecondaryBuckets, recordingState.SecondaryBucketByStart, opIndex, out VulkanSecondaryRecordingBucket transferBucket) &&
                                        TryRecordSecondaryBucket(
                                            primaryCommandBuffer: recordingState.CommandBuffer,
                                            recordingState.FrameDataImageIndex,
                                            recordingState.ExecutedCommandChainSecondaryHandles,
                                            recordingState.Ops,
                                            opIndex,
                                            transferBucket,
                                            opPassIndex,
                                            recordingState.RenderScope.IsActive,
                                            recordingState.ActiveInlineQuery is not null,
                                            bufferCopyOp.Label))
                                    {
                                        opIndex = opIndex + transferBucket.Count - 1;
                                    }
                                    else
                                    {
                                        CmdBeginLabel(recordingState.CommandBuffer, bufferCopyOp.Label);
                                        RecordBufferCopyOp(recordingState.CommandBuffer, bufferCopyOp);
                                        CmdEndLabel(recordingState.CommandBuffer);
                                    }
                                    break;

                                case EVulkanPrimaryPlanNodeKind.SubmissionMarker:
                                    SubmissionMarkerOp submissionMarkerOp =
                                        (SubmissionMarkerOp)op;
                                    RegisterSubmissionMarker(recordingState.CommandBuffer, submissionMarkerOp.Fence);
                                    break;

                                case EVulkanPrimaryPlanNodeKind.MemoryBarrier:
                                    MemoryBarrierOp memoryBarrierOp =
                                        (MemoryBarrierOp)op;
                                    CmdBeginLabel(recordingState.CommandBuffer, "MemoryBarrier");
                                    EmitMemoryBarrierMask(recordingState.CommandBuffer, memoryBarrierOp.Mask);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    break;

                                case EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling:
                                    PublishFramebufferForSamplingOp publishOp =
                                        (PublishFramebufferForSamplingOp)op;
                                    CmdBeginLabel(recordingState.CommandBuffer, "PublishFramebufferForSampling");
                                    RecordPublishFramebufferForSamplingOp(recordingState.CommandBuffer, publishOp);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    break;

                                case EVulkanPrimaryPlanNodeKind.DlssUpscale:
                                    DlssUpscaleOp dlssOp = (DlssUpscaleOp)op;
                                    CmdBeginLabel(recordingState.CommandBuffer, "DLSS.SuperResolution");
                                    RecordDlssUpscaleOp(recordingState.CommandBuffer, dlssOp);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    break;
                                case EVulkanPrimaryPlanNodeKind.DlssFrameGeneration:
                                    DlssFrameGenerationOp frameGenerationOp =
                                        (DlssFrameGenerationOp)op;
                                    CmdBeginLabel(recordingState.CommandBuffer, "DLSS.FrameGenerationInputs");
                                    RecordDlssFrameGenerationOp(recordingState.CommandBuffer, recordingState.FrameDataImageIndex, frameGenerationOp);
                                    CmdEndLabel(recordingState.CommandBuffer);
                                    break;
                            }
                        }
                        finally
                        {
                            frameOpProfileScope?.Dispose();
                        }
                    }
                    catch (Exception opEx)
                    {
                        recordingState.Metrics.DroppedFrameOps++;
                        if (op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
                            recordingState.Metrics.DroppedDrawOps++;
                        if (op is ComputeDispatchOp or ComputeDispatchIndirectOp)
                            recordingState.Metrics.DroppedComputeOps++;
                        recordingState.Metrics.FirstFailure ??= CaptureFrameOpFailure(op, opEx);

                        EndActiveRenderPass(ref recordingState);
                        if (recordingState.RenderPassLabelActive)
                        {
                            CmdEndLabel(recordingState.CommandBuffer);
                            recordingState.RenderPassLabelActive = false;
                        }

                        string opContext = BuildFrameOpFailureContext(op);

                        Debug.VulkanEvery(
                            $"Vulkan.FrameOpError.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Frame op recording failed for {0}: {1}: {2}{3}{4}",
                            op.GetType().Name,
                            opEx.GetType().Name,
                            opEx.Message,
                            opContext,
                            opEx.StackTrace is { Length: > 0 } ? Environment.NewLine + opEx.StackTrace : string.Empty);

                        // Continue recording remaining ops instead of aborting the
                        // entire command buffer.  A single broken shader/pipeline
                        // should not prevent the rest of the frame from rendering.
                        continue;
                    }
                }

            }
        }

    }
}
