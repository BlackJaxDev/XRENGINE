using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Primary recorder dispatch over the sealed, dense frame-operation stream.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private bool RecordPrimaryOperations(scoped ref PrimaryCommandBufferRecordingState recordingState)
    {
        using var mainLoopProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.MainOpLoop");
        for (int operationIndex = 0; operationIndex < recordingState.Ops.Length; operationIndex++)
        {
            if (recordingState.PipelineDeferredOperationIndices.Contains(operationIndex))
                continue;

            ref readonly FrameOperationHeader header = ref recordingState.Ops.GetHeader(operationIndex);
            ref readonly VulkanPrimaryPlanNode primaryNode = ref recordingState.PrimaryCommandPlan.GetNode(operationIndex);
            if (primaryNode.OperationIndex != operationIndex)
                throw new VulkanPlanPreconditionException("A terminal or mismatched primary-plan node appeared in the frame-operation range.");

            try
            {
                if (!header.RequiresPrimaryRecordingContext)
                {
                    using (VulkanCpuStageScope dispatchStage =
                           new(
                               _frameTelemetry,
                               header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw
                                   ? EVulkanCpuStage.PrimaryMeshOperation
                                   : EVulkanCpuStage.PrimaryNonMeshOperation))
                    {
                        operationIndex = RecordTypedPrimaryOperation(ref recordingState, in primaryNode, in header, operationIndex);
                    }
                    if (recordingState.CommandChainPublicationDeferred)
                        return false;
                    continue;
                }

                int passIndex;
                using (VulkanCpuStageScope preparationStage =
                       new(_frameTelemetry, EVulkanCpuStage.PrimaryOperationPreparation))
                {
                    if (!TryPreparePrimaryOperation(ref recordingState, in primaryNode, in header, operationIndex, out passIndex))
                        continue;
                }

                using (VulkanCpuStageScope dispatchStage =
                       new(
                           _frameTelemetry,
                           header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw
                               ? EVulkanCpuStage.PrimaryMeshOperation
                               : EVulkanCpuStage.PrimaryNonMeshOperation))
                {
                    operationIndex = RecordTypedPrimaryOperation(ref recordingState, in primaryNode, in header, operationIndex, passIndex);
                }
                if (recordingState.CommandChainPublicationDeferred)
                    return false;
            }
            catch (Exception exception)
            {
                if (exception is VulkanPlanPreconditionException)
                    throw;
                HandlePrimaryOperationRecordingFailure(ref recordingState, in header, operationIndex, exception);
            }
        }
        return true;
    }

    private int RecordTypedPrimaryOperation(scoped ref PrimaryCommandBufferRecordingState state, in VulkanPrimaryPlanNode node, in FrameOperationHeader header, int index, int passIndex = int.MinValue)
    {
        int resolvedPass = passIndex == int.MinValue ? header.PassIndex : passIndex;
        VulkanPrimaryOperationRecordingInfo info = new(node.Actions, index, resolvedPass);
        if (info.EndsRendering && state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        RecordVulkanCommandDiagnosticMarker(state.CommandBuffer, header.OpCode, resolvedPass, index);
        return header.OpCode switch
        {
            EVulkanPrimaryPlanNodeKind.TextureUpload => RecordTextureUploadPayload(ref state, in state.Ops.GetTextureUpload(index), in info),
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => RecordMemoryBarrierPayload(ref state, in state.Ops.GetMemoryBarrier(index), in info),
            EVulkanPrimaryPlanNodeKind.SubmissionMarker => RecordSubmissionMarkerPayload(ref state, in state.Ops.GetSubmissionMarker(index), in info),
            EVulkanPrimaryPlanNodeKind.BufferCopy => RecordBufferCopyPayload(ref state, in state.Ops.GetBufferCopy(index), in info),
            EVulkanPrimaryPlanNodeKind.ComputeDispatch => RecordComputeDispatchPayload(ref state, in state.Ops.GetComputeDispatch(index), in info),
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect => RecordComputeDispatchIndirectPayload(ref state, in state.Ops.GetComputeDispatchIndirect(index), in info),
            EVulkanPrimaryPlanNodeKind.Query => RecordQueryPayload(ref state, in state.Ops.GetQuery(index), in info),
            EVulkanPrimaryPlanNodeKind.TransformFeedback => RecordTransformFeedbackPayload(ref state, in state.Ops.GetTransformFeedback(index), in info),
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount => RecordMeshTaskPayload(ref state, in state.Ops.GetMeshTask(index), in info),
            EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling => RecordPublishFramebufferPayload(ref state, in state.Ops.GetPublishedFramebuffer(index), in info),
            EVulkanPrimaryPlanNodeKind.DlssUpscale => RecordDlssUpscalePayload(ref state, in state.Ops.GetDlssUpscale(index), in info),
            EVulkanPrimaryPlanNodeKind.DlssFrameGeneration => RecordDlssFrameGenerationPayload(ref state, in state.Ops.GetDlssFrameGeneration(index), in info),
            EVulkanPrimaryPlanNodeKind.MeshDraw => RecordMeshDrawPayload(ref state, in state.Ops.GetMeshDraw(index), in info),
            EVulkanPrimaryPlanNodeKind.IndirectDraw => RecordIndirectDrawPayload(ref state, in state.Ops.GetIndirectDraw(index), in info),
            EVulkanPrimaryPlanNodeKind.Clear => RecordClearPayload(ref state, in state.Ops.GetClear(index), in info),
            EVulkanPrimaryPlanNodeKind.Blit => RecordBlitPayload(ref state, in state.Ops.GetBlit(index), in info),
            _ => throw new VulkanPlanPreconditionException($"Typed primary dispatch for '{header.OpCode}' has not been published."),
        };
    }

    private int RecordTextureUploadPayload(scoped ref PrimaryCommandBufferRecordingState state, in TextureUploadPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        if (info.EndsRendering) EndActiveRenderPass(ref state);
        if (state.PassIndexLabelActive) { _deviceContext.CmdEndLabel(state.CommandBuffer); state.PassIndexLabelActive = false; }
        CmdBeginLabel(state.CommandBuffer, "TextureUpload");
        RecordTextureUploadOp(state.CommandBuffer, payload.Upload);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordMemoryBarrierPayload(scoped ref PrimaryCommandBufferRecordingState state, in MemoryBarrierPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "MemoryBarrier");
        EmitMemoryBarrierMask(state.CommandBuffer, payload.Mask);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordSubmissionMarkerPayload(scoped ref PrimaryCommandBufferRecordingState state, in SubmissionMarkerPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        RegisterSubmissionMarker(state.CommandBuffer, payload.Fence);
        return info.OperationIndex;
    }

    private int RecordBufferCopyPayload(scoped ref PrimaryCommandBufferRecordingState state, in BufferCopyPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, payload.Label);
        RecordBufferCopyPayload(state.CommandBuffer, in payload);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordComputeDispatchPayload(scoped ref PrimaryCommandBufferRecordingState state, in ComputeDispatchPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "ComputeDispatch");
        EnsureComputeSampledImageLayoutsForDispatch(state.CommandBuffer, payload.Snapshot);
        ref readonly FrameOperationHeader header = ref state.Ops.GetHeader(info.OperationIndex);
        ref readonly FrameOpContext context = ref state.Ops.GetContext(info.OperationIndex);
        ulong descriptorKey = ComputeReusableComputeDescriptorBindingKey(
            in payload,
            in header,
            in context,
            ResolveCommandChainInlineOperationIndex(state.Ops.Stream, info.OperationIndex));
        RecordComputeDispatchPayload(state.CommandBuffer, state.FrameDataImageIndex, in payload, descriptorKey);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordComputeDispatchIndirectPayload(scoped ref PrimaryCommandBufferRecordingState state, in ComputeDispatchIndirectPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, payload.Label);
        EnsureComputeSampledImageLayoutsForDispatch(state.CommandBuffer, payload.Snapshot);
        RecordComputeDispatchIndirectPayload(state.CommandBuffer, state.FrameDataImageIndex, in payload);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.Operation == ERenderQueryOperation.CopyResults &&
            payload.Query.CopyResults(state.CommandBuffer, payload.ResultDestination, payload.ResultDestinationOffset, payload.ResultStride, payload.IncludeAvailability) != ERenderQueryReadStatus.Ready)
            state.FrameOpsRequireRerecordLocal = true;
        else if (payload.Operation == ERenderQueryOperation.WriteTimestamp && state.RecordingScratch.PreparedInlineQueries.Contains(payload.Query) &&
                 payload.Query.WriteTimestamp(state.CommandBuffer, payload.TimestampStage, payload.PointIndex) != ERenderQueryReadStatus.Ready)
            state.FrameOpsRequireRerecordLocal = true;
        else if (payload.Operation == ERenderQueryOperation.WriteProperties &&
                 (!state.RecordingScratch.PreparedInlineQueries.Contains(payload.Query) || payload.Query.WriteProperties(CreateQueryCommandEncoder(), state.CommandBuffer, payload.SourceHandles.Span) != ERenderQueryReadStatus.Ready))
            state.FrameOpsRequireRerecordLocal = true;
        else if (payload.Operation is ERenderQueryOperation.Begin or ERenderQueryOperation.End)
        {
            XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
            if (info.BeginsRendering && (!state.RenderScope.IsActive || state.RenderScope.Target != target)) { EndActiveRenderPass(ref state); BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext); }
            bool label = CanRecordCommandBufferDebugLabels && CmdBeginLabel(state.CommandBuffer, $"Query.{payload.Operation}");
            if (payload.Operation == ERenderQueryOperation.Begin) BeginInlineQueryPayload(ref state, in payload, info.OperationIndex, info.PassIndex);
            else EndInlineQueryPayload(ref state, in payload, info.OperationIndex, info.PassIndex);
            if (label) CmdEndLabel(state.CommandBuffer);
        }
        return info.OperationIndex;
    }

    private static void BeginInlineQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, int index, int pass)
    {
        VkRenderQuery query = payload.Query;
        if (state.ActiveInlineQuery is not null) { state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer); return; }
        if (state.RecordingScratch.PreparedInlineQueries.Contains(query) && state.RecordingScratch.BegunInlineQueries.Add(query))
        {
            state.ActiveInlineQuery = query.BeginQuery(state.CommandBuffer) == ERenderQueryReadStatus.Ready ? query : null;
            if (state.ActiveInlineQuery is null) state.FrameOpsRequireRerecordLocal = true;
            state.ActiveInlineQueryRecordedDraw = false;
            return;
        }
        if (state.RecordingScratch.PreparedInlineQueries.Contains(query)) state.FrameOpsRequireRerecordLocal = true;
    }

    private static void EndInlineQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, int index, int pass)
    {
        VkRenderQuery query = payload.Query;
        if (ReferenceEquals(state.ActiveInlineQuery, query))
        {
            if (!state.ActiveInlineQueryRecordedDraw) { state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer); }
            query.EndQuery(state.CommandBuffer); state.ActiveInlineQuery = null; state.ActiveInlineQueryRecordedDraw = false; return;
        }
        state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer);
    }

    private int RecordTransformFeedbackPayload(scoped ref PrimaryCommandBufferRecordingState state, in TransformFeedbackPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        if (info.BeginsRendering && (!state.RenderScope.IsActive || state.RenderScope.Target != target)) { EndActiveRenderPass(ref state); BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext); }
        bool labelActive = CanRecordCommandBufferDebugLabels && CmdBeginLabel(state.CommandBuffer, $"TransformFeedback.{payload.Operation}");
        RecordTransformFeedbackPayload(state.CommandBuffer, in payload);
        if (labelActive) CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordMeshTaskPayload(scoped ref PrimaryCommandBufferRecordingState state, in MeshTaskDispatchIndirectCountPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        // vkCmdPipelineBarrier is not legal while either a legacy render pass or a
        // dynamic-rendering scope is active. The mesh-task command/count buffers
        // are produced by compute or transfer work, so publish their indirect and
        // shader reads before opening the graphics scope that consumes them.
        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);
        EmitMeshTaskDispatchIndirectCountReadBarrier(ref state);
        if (info.BeginsRendering)
            BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext);
        CmdBeginLabel(state.CommandBuffer, "MeshTaskDispatchIndirectCount");
        RecordMeshTaskDispatchIndirectCountPayload(state.CommandBuffer, state.FrameDataImageIndex, in payload);
        CmdEndLabel(state.CommandBuffer);
        if (target is null)
            state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private unsafe void EmitMeshTaskDispatchIndirectCountReadBarrier(scoped ref PrimaryCommandBufferRecordingState state)
    {
        MemoryBarrier memoryBarrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit,
        };

        CmdPipelineBarrierTracked(
            state.CommandBuffer,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.DrawIndirectBit |
            PipelineStageFlags.TaskShaderBitNV |
            PipelineStageFlags.MeshShaderBitNV,
            DependencyFlags.None,
            1,
            &memoryBarrier,
            0,
            null,
            0,
            null);

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
    }

    private int RecordPublishFramebufferPayload(scoped ref PrimaryCommandBufferRecordingState state, in PublishFramebufferPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "PublishFramebufferForSampling");
        RecordPublishFramebufferForSamplingPayload(state.CommandBuffer, payload.FrameBuffer);
        CmdEndLabel(state.CommandBuffer); return info.OperationIndex;
    }

    private int RecordDlssUpscalePayload(scoped ref PrimaryCommandBufferRecordingState state, in DlssUpscalePayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "DLSS.SuperResolution");
        RecordDlssUpscalePayload(state.CommandBuffer, in payload);
        CmdEndLabel(state.CommandBuffer); return info.OperationIndex;
    }

    private int RecordDlssFrameGenerationPayload(scoped ref PrimaryCommandBufferRecordingState state, in DlssFrameGenerationPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "DLSS.FrameGenerationInputs");
        RecordDlssFrameGenerationPayload(state.CommandBuffer, state.ImageIndex, in payload);
        CmdEndLabel(state.CommandBuffer); return info.OperationIndex;
    }

    private int RecordMeshDrawPayload(scoped ref PrimaryCommandBufferRecordingState state, in MeshDrawPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        if (state.CommandChainSchedule is not null &&
            state.ScheduledCommandChainKeysByOpIndex is not null &&
            state.ScheduledCommandChainCache is not null &&
            TryGetScheduledCommandChainForOp(
                ref state,
                info.OperationIndex,
                out _,
                out _))
        {
            int scheduledRunCount = CountContiguousMeshCommandChainRun(
                ref state,
                info.OperationIndex,
                in payload,
                info.PassIndex);
            if (scheduledRunCount > 0 &&
                TryExecuteScheduledMeshCommandChainSecondaryRun(
                    ref state,
                    info.OperationIndex,
                    scheduledRunCount,
                    info.PassIndex))
            {
                if (target is null)
                    state.ActualSwapchainWriteCount += scheduledRunCount;
                return info.OperationIndex + scheduledRunCount - 1;
            }
        }

        if (info.BeginsRendering && !state.RenderScope.MatchesTarget(target))
        {
            EndActiveRenderPass(ref state);
            int slot = GetMeshDrawUniformSlot(ref state, info.OperationIndex, payload.Draw.Renderer, state.ActiveContext, payload.Draw);
            payload.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(state.CommandBuffer, payload.Draw, slot, state.CommandBufferImageSlot, target, info.PassIndex, state.ActiveContext.PassMetadata);
            BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext);
        }
        int uniformSlot = GetMeshDrawUniformSlot(ref state, info.OperationIndex, payload.Draw.Renderer, state.ActiveContext, payload.Draw);
        bool recorded = RecordMeshDrawPayloadIntoCommandBuffer(ref state, state.CommandBuffer, in payload, target, state.ActiveContext, info.PassIndex, uniformSlot);
        if (state.ActiveInlineQuery is not null && recorded) state.ActiveInlineQueryRecordedDraw = true;
        if (target is null) state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private int RecordClearPayload(scoped ref PrimaryCommandBufferRecordingState state, in ClearPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        if (info.BeginsRendering && (!state.RenderScope.IsActive || state.RenderScope.Target != target)) { EndActiveRenderPass(ref state); BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext); }
        uint layers = state.RenderScope.UsesDynamicRendering ? Math.Max(state.RenderScope.DynamicRenderingFormats.LayerCount, 1u) : 0u;
        uint viewMask = state.RenderScope.UsesDynamicRendering ? state.RenderScope.DynamicRenderingFormats.ViewMask : 0u;
        bool recorded = false;
        if (target is null && state.SwapchainClearedThisFrame && payload.ClearColor)
        {
            if (payload.ClearDepth || payload.ClearStencil) { RecordClearPayload(state.CommandBuffer, state.ImageIndex, in payload, target, state.RenderScope.RenderArea, in state.SwapchainTarget, layers, viewMask, true); recorded = true; }
        }
        else { RecordClearPayload(state.CommandBuffer, state.ImageIndex, in payload, target, state.RenderScope.RenderArea, in state.SwapchainTarget, layers, viewMask); recorded = true; }
        if (target is null && recorded) state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private int RecordBlitPayload(scoped ref PrimaryCommandBufferRecordingState state, in BlitPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.ColorBit && (payload.InFbo is null || payload.OutFbo is null)) EnsureSwapchainColorAttachmentLayoutForBlit(ref state);
        CmdBeginLabel(state.CommandBuffer, "Blit");
        bool recorded = RecordBlitPayload(state.CommandBuffer, state.ImageIndex, payload, in state.SwapchainTarget, exactColorSource: null);
        CmdEndLabel(state.CommandBuffer);
        if (payload.OutFbo is null && (payload.ColorBit || payload.DepthBit || payload.StencilBit) && recorded) { state.SwapchainWrittenOutsideRenderPass = true; if (payload.ColorBit) { state.SwapchainInColorAttachmentLayout = true; state.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal; } state.ActualSwapchainWriteCount++; }
        return info.OperationIndex;
    }

    private int RecordIndirectDrawPayload(scoped ref PrimaryCommandBufferRecordingState state, in IndirectDrawPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        EmitIndirectDrawRunReadBarrier(ref state);
        if (info.BeginsRendering) BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext);
        CmdBeginLabel(state.CommandBuffer, "IndirectDraw");
        RecordIndirectDrawPayloadIntoCommandBuffer(ref state, state.CommandBuffer, in payload, target, state.ActiveContext, info.PassIndex, info.OperationIndex);
        CmdEndLabel(state.CommandBuffer);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(false, false, 1);
        if (target is null) state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private bool TryPreparePrimaryOperation(scoped ref PrimaryCommandBufferRecordingState state, in VulkanPrimaryPlanNode node, in FrameOperationHeader header, int operationIndex, out int passIndex)
    {
        ref readonly FrameOpContext context = ref state.Ops.GetContext(operationIndex);
        if (!UpdatePrimaryRecordingContext(ref state, in context, state.Ops.GetTarget(operationIndex), header.PassIndex)) { passIndex = int.MinValue; return false; }
        passIndex = header.PassIndex;
        if (passIndex == int.MinValue) { RecordDroppedPrimaryOperation(ref state, header.OpCode); return false; }
        if (state.SkipUiPipelineOps && context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline) { RecordDroppedPrimaryOperation(ref state, header.OpCode); return false; }
        TransitionToPrimaryOperationPass(ref state, in node, in header, operationIndex, passIndex);
        return true;
    }

    private bool UpdatePrimaryRecordingContext(scoped ref PrimaryCommandBufferRecordingState state, in FrameOpContext context, XRFrameBuffer? target, int passIndex)
    {
        if (state.HasActiveContext && FrameOpContextCompatibility.AreRecordingCompatible(state.ActiveContext, context)) return true;
        bool preserve = state.RenderScope.ShouldPreserveForContextChange(target is null, target, passIndex, state.ActiveInlineQuery is not null, context.SchedulingIdentity, state.ActivePassIndex, state.ActiveSchedulingIdentity, FrameOpContextCompatibility.AreQueryScopeCompatible(state.ActiveContext, context));
        if (!preserve) EndActiveRenderPass(ref state);
        if (!preserve && state.PassIndexLabelActive) { _deviceContext.CmdEndLabel(state.CommandBuffer); state.PassIndexLabelActive = false; }
        state.ActiveContext = context; state.HasActiveContext = true; ApplyPipelineOverride(ref state, state.ActiveContext);
        if (!UpdatePrimaryResourcePlannerContext(ref state)) return false;
        if (preserve) state.ActiveSchedulingIdentity = context.SchedulingIdentity; else { state.ActivePassIndex = int.MinValue; state.ActiveSchedulingIdentity = int.MinValue; }
        return true;
    }

    private bool UpdatePrimaryResourcePlannerContext(scoped ref PrimaryCommandBufferRecordingState state)
    {
        state.RenderGraphPlan = ResolvePrimaryRenderGraphPlan(ref state, in state.ActiveContext);
        state.PlannerContext = state.ActiveContext;
        state.HasPlannerContext = true;
        return true;
    }

    private static VulkanRenderGraphPlan ResolvePrimaryRenderGraphPlan(scoped ref PrimaryCommandBufferRecordingState state, in FrameOpContext context)
    {
        if (state.FramePlan is not null && state.FramePlan.TryResolveRenderGraphPlan(in context, out VulkanRenderGraphPlan plan)) return plan;
        if (context.ResourceRegistry is null && context.PassMetadata is not { Count: > 0 }) return state.RenderGraphPlan;
        throw new VulkanPlanPreconditionException($"Primary recording has no frozen render-graph publication for kind={context.ContextKind} pipe={context.PipelineIdentity} viewport={context.ViewportIdentity} resourceGeneration={context.ResourceGeneration}.");
    }

    private void TransitionToPrimaryOperationPass(scoped ref PrimaryCommandBufferRecordingState state, in VulkanPrimaryPlanNode node, in FrameOperationHeader header, int operationIndex, int passIndex)
    {
        int schedulingIdentity = state.Ops.GetContext(operationIndex).SchedulingIdentity;
        if (!HasPrimaryPlanAction(node.Actions, EVulkanPrimaryPlanAction.BarrierBatch) || (passIndex == state.ActivePassIndex && schedulingIdentity == state.ActiveSchedulingIdentity)) return;
        EndActiveRenderPass(ref state);
        if (state.PassIndexLabelActive) { _deviceContext.CmdEndLabel(state.CommandBuffer); state.PassIndexLabelActive = false; }
        if (_deviceContext.CanRecordCommandBufferDebugLabels) state.PassIndexLabelActive = _deviceContext.CmdBeginLabel(state.CommandBuffer, $"Pass={passIndex} Pipe={state.ActiveContext.PipelineIdentity} Vp={state.ActiveContext.ViewportIdentity}");
        EmitPassBarriers(ref state, passIndex);
        state.ActivePassIndex = passIndex; state.ActiveSchedulingIdentity = schedulingIdentity;
    }

    private void HandlePrimaryOperationRecordingFailure(scoped ref PrimaryCommandBufferRecordingState state, in FrameOperationHeader header, int operationIndex, Exception exception)
    {
        RecordDroppedPrimaryOperation(ref state, header.OpCode, true);
        EndActiveRenderPass(ref state);
        Debug.VulkanEvery($"Vulkan.FrameOpError.{GetHashCode()}", TimeSpan.FromSeconds(1), "[Vulkan] Frame op recording failed for {0}: {1}: {2}", header.OpCode, exception.GetType().Name, exception.Message);
    }

    private static void RecordDroppedPrimaryOperation(scoped ref PrimaryCommandBufferRecordingState state, EVulkanPrimaryPlanNodeKind kind, bool countIndirectCompute = false)
    {
        state.Metrics.DroppedFrameOps++;
        if (kind is EVulkanPrimaryPlanNodeKind.MeshDraw or EVulkanPrimaryPlanNodeKind.IndirectDraw or EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount) state.Metrics.DroppedDrawOps++;
        if (kind == EVulkanPrimaryPlanNodeKind.ComputeDispatch || countIndirectCompute && kind == EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect) state.Metrics.DroppedComputeOps++;
    }

    private static bool HasPrimaryPlanAction(EVulkanPrimaryPlanAction actions, EVulkanPrimaryPlanAction action) => (actions & action) != 0;
}
