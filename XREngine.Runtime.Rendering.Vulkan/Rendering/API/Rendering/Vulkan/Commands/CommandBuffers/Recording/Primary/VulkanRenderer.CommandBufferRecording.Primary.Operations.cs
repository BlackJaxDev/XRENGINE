using System;

namespace XREngine.Rendering.Vulkan;

    internal sealed unsafe partial class VulkanCommandRuntime
{
    private bool RecordPrimaryOperations(
        scoped ref PrimaryCommandBufferRecordingState recordingState)
    {
        using var mainLoopProfileScope =
            RuntimeRenderingHostServices.Profiling.StartProfileScope(
                "Vulkan.RecordPrimary.MainOpLoop");

        for (int operationIndex = 0;
             operationIndex < recordingState.Ops.Length;
             operationIndex++)
        {
            FrameOp operation = recordingState.Ops[operationIndex];
            if (recordingState.PipelineDeferredOps.Contains(operation))
                continue;

            ref readonly VulkanPrimaryPlanNode primaryNode =
                ref recordingState.PrimaryCommandPlan.GetNode(operationIndex);
            if (!ReferenceEquals(primaryNode.Operation, operation))
            {
                throw new VulkanPlanPreconditionException(
                    "A terminal or mismatched primary-plan node appeared in the frame-operation range.");
            }

            try
            {
                if (!operation.RequiresPrimaryRecordingContext)
                {
                    operationIndex = RecordContextIndependentPrimaryOperation(
                        ref recordingState,
                        in primaryNode,
                        operation,
                        operationIndex);
                    continue;
                }

                if (!TryPreparePrimaryOperation(
                        ref recordingState,
                        in primaryNode,
                        operation,
                        operationIndex,
                        out int passIndex))
                {
                    continue;
                }

                operationIndex = RecordPreparedPrimaryOperation(
                    ref recordingState,
                    in primaryNode,
                    operation,
                    operationIndex,
                    passIndex);
            }
            catch (Exception exception)
            {
                if (exception is VulkanPlanPreconditionException)
                    throw;

                HandlePrimaryOperationRecordingFailure(
                    ref recordingState,
                    operation,
                    exception);
            }
        }

        return true;
    }

    private int RecordContextIndependentPrimaryOperation(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryPlanNode primaryNode,
        FrameOp operation,
        int operationIndex)
    {
        VulkanPrimaryOperationRecordingInfo recordingInfo = new(
            primaryNode.Actions,
            operationIndex,
            operation.PassIndex);
        return operation.RecordPrimary(this, ref recordingState, in recordingInfo);
    }

    private bool TryPreparePrimaryOperation(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryPlanNode primaryNode,
        FrameOp operation,
        int operationIndex,
        out int passIndex)
    {
        if (!UpdatePrimaryRecordingContext(ref recordingState, operation))
        {
            passIndex = int.MinValue;
            return false;
        }

        passIndex = operation.PassIndex;

        if (passIndex == int.MinValue)
        {
            RecordDroppedPrimaryOperation(ref recordingState, operation);
            recordingState.Metrics.FirstFailure ??= CaptureFrameOpFailure(
                operation,
                new InvalidOperationException(
                    "No valid render-graph pass index could be resolved."));
            Debug.VulkanWarningEvery(
                $"Vulkan.OpDroppedNoPass.{GetFrameOpDiagnosticName(operation)}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Dropping op '{0}' because no valid render-graph pass index could be resolved.",
                GetFrameOpDiagnosticName(operation));
            return false;
        }

        if (recordingState.SkipUiPipelineOps &&
            operation.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
        {
            RecordDroppedPrimaryOperation(ref recordingState, operation);
            Debug.VulkanEvery(
                $"Vulkan.SkipUiPipeline.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping UI pipeline op {0} pass={1} pipe={2} due to XRE_SKIP_UI_PIPELINE=1.",
                GetFrameOpDiagnosticName(operation),
                passIndex,
                operation.Context.PipelineIdentity);
            return false;
        }

        if (recordingState.SkipUiBatchTextOps && IsUiBatchTextDrawOp(operation))
        {
            recordingState.Metrics.DroppedFrameOps++;
            recordingState.Metrics.DroppedDrawOps++;
            Debug.VulkanEvery(
                $"Vulkan.SkipUiBatchText.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping batched UI text op pass={0} pipe={1} due to XRE_SKIP_UI_BATCH_TEXT=1.",
                passIndex,
                operation.Context.PipelineIdentity);
            return false;
        }

        TransitionToPrimaryOperationPass(
            ref recordingState,
            in primaryNode,
            operation,
            operationIndex,
            passIndex);
        return true;
    }

    private bool UpdatePrimaryRecordingContext(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        FrameOp operation)
    {
        if (recordingState.HasActiveContext &&
            FrameOpContextCompatibility.AreRecordingCompatible(
                recordingState.ActiveContext,
                operation.Context))
            return true;

        IDisposable? contextChangeProfileScope = null;
        if (CommandRecordingDetailProfilingEnabled)
        {
            contextChangeProfileScope =
                RuntimeRenderingHostServices.Profiling.StartProfileScope(
                    "Vulkan.RecordPrimary.ContextChange");
        }

        try
        {
            // Query begin/draw/end capture their contexts independently. Resource
            // generations may advance without splitting an otherwise compatible
            // query scope. Swapchain scopes are likewise preserved when possible
            // so a store/layout/load cycle cannot discard composited content.
            bool preservedRenderPass = recordingState.RenderScope.ShouldPreserveForContextChange(
                VulkanSwapchainContextCoalescer.TargetsSwapchain(operation),
                operation.Target,
                operation.PassIndex,
                recordingState.ActiveInlineQuery is not null,
                operation.Context.SchedulingIdentity,
                recordingState.ActivePassIndex,
                recordingState.ActiveSchedulingIdentity,
                FrameOpContextCompatibility.AreQueryScopeCompatible(
                    recordingState.ActiveContext,
                    operation.Context));

            if (!preservedRenderPass)
                EndActiveRenderPass(ref recordingState);

            if (!preservedRenderPass && recordingState.PassIndexLabelActive)
            {
                CmdEndLabel(recordingState.CommandBuffer);
                recordingState.PassIndexLabelActive = false;
            }

            recordingState.ActiveContext = operation.Context;
            recordingState.HasActiveContext = true;
            ApplyPipelineOverride(ref recordingState, recordingState.ActiveContext);
            if (!UpdatePrimaryResourcePlannerContext(ref recordingState))
                return false;

            if (preservedRenderPass)
            {
                recordingState.ActiveSchedulingIdentity =
                    operation.Context.SchedulingIdentity;
            }
            else
            {
                recordingState.ActivePassIndex = int.MinValue;
                recordingState.ActiveSchedulingIdentity = int.MinValue;
            }

            return true;
        }
        finally
        {
            contextChangeProfileScope?.Dispose();
        }
    }

    private bool UpdatePrimaryResourcePlannerContext(
        scoped ref PrimaryCommandBufferRecordingState recordingState)
    {
        // Planner-state selection and wrapper publication are complete before
        // the frame loop freezes the input. Encoding may observe the prepared
        // context identity, but must never switch or rebuild planner state.
        recordingState.RenderGraphPlan = ResolvePrimaryRenderGraphPlan(
            ref recordingState,
            in recordingState.ActiveContext);
        recordingState.PlannerContext = recordingState.ActiveContext;
        recordingState.HasPlannerContext = true;
        return true;
    }

    private static VulkanRenderGraphPlan ResolvePrimaryRenderGraphPlan(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in FrameOpContext context)
    {
        if (recordingState.FramePlan is not null &&
            recordingState.FramePlan.TryResolveRenderGraphPlan(
                in context,
                out VulkanRenderGraphPlan plan))
        {
            return plan;
        }
        if (context.ResourceRegistry is null && context.PassMetadata is not { Count: > 0 })
            return recordingState.RenderGraphPlan;

        throw new VulkanPlanPreconditionException(
            $"Primary recording has no frozen render-graph publication for " +
            $"kind={context.ContextKind} pipe={context.PipelineIdentity} " +
            $"viewport={context.ViewportIdentity} resourceGeneration={context.ResourceGeneration}.");
    }

    private void TransitionToPrimaryOperationPass(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryPlanNode primaryNode,
        FrameOp operation,
        int operationIndex,
        int passIndex)
    {
        int schedulingIdentity = operation.Context.SchedulingIdentity;
        if (!HasPrimaryPlanAction(
                primaryNode.Actions,
                EVulkanPrimaryPlanAction.BarrierBatch) ||
            passIndex == recordingState.ActivePassIndex &&
            schedulingIdentity == recordingState.ActiveSchedulingIdentity)
            return;

        IDisposable? passTransitionProfileScope = null;
        if (CommandRecordingDetailProfilingEnabled)
        {
            passTransitionProfileScope =
                RuntimeRenderingHostServices.Profiling.StartProfileScope(
                    "Vulkan.RecordPrimary.PassTransition");
        }

        try
        {
            using VulkanCpuStageScope transitionStage =
                new(_frameTelemetry, EVulkanCpuStage.ContextPassTransitions);
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
                    $"Pass={passIndex} Pipe={operation.Context.PipelineIdentity} Vp={operation.Context.ViewportIdentity}");
            }

            using (VulkanCpuStageScope barrierStage =
                   new(_frameTelemetry, EVulkanCpuStage.BarrierPlanningEmission))
            {
                int emittedQueueOwnershipTransfers =
                    EmitPassBarriers(ref recordingState, passIndex);
                bool plannedQueueOwnershipTransfer = HasPrimaryPlanAction(
                    primaryNode.Actions,
                    EVulkanPrimaryPlanAction.QueueOwnershipTransfer);
                if (plannedQueueOwnershipTransfer !=
                    (emittedQueueOwnershipTransfers > 0))
                {
                    throw new VulkanPlanPreconditionException(
                        $"Primary plan queue-ownership action mismatch for pass {passIndex}: " +
                        $"planned={plannedQueueOwnershipTransfer} emitted={emittedQueueOwnershipTransfers}.");
                }
            }

            TransitionFrameOpDescriptorSnapshotsForSampling(
                recordingState.CommandBuffer,
                recordingState.Ops,
                operationIndex,
                passIndex,
                schedulingIdentity,
                recordingState.MeshDrawUniformSlotsByOpIndex,
                recordingState.MeshDrawSlotsByRendererFamily,
                recordingState.MeshFrameDataFamilyBases,
                recordingState.CommandBufferImageSlot,
                recordingState.ScheduledCommandChainKeysByOpIndex,
                recordingState.ScheduledCommandChainCache);
            recordingState.ActivePassIndex = passIndex;
            recordingState.ActiveSchedulingIdentity = schedulingIdentity;
        }
        finally
        {
            passTransitionProfileScope?.Dispose();
        }
    }

    private int RecordPreparedPrimaryOperation(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryPlanNode primaryNode,
        FrameOp operation,
        int operationIndex,
        int passIndex)
    {
        RecordVulkanCommandDiagnosticMarker(
            recordingState.CommandBuffer,
            operation,
            passIndex,
            operationIndex);
        using var vulkanGpuScope = TryBeginVulkanGpuProfilerScope(
            recordingState.CommandBuffer,
            operation,
            passIndex);

        IDisposable? operationProfileScope = null;
        if (CommandRecordingDetailProfilingEnabled)
        {
            operationProfileScope =
                RuntimeRenderingHostServices.Profiling.StartProfileScope(
                    GetRecordPrimaryFrameOpProfileScopeName(operation));
        }

        try
        {
            using VulkanCpuStageScope operationDispatchStage =
                new(_frameTelemetry, EVulkanCpuStage.OpDispatch);
            System.Diagnostics.Debug.Assert(
                HasPrimaryPlanAction(
                    primaryNode.Actions,
                    EVulkanPrimaryPlanAction.RecordOperation),
                "Every semantic primary-plan node must publish an operation-record action.");

            if (HasPrimaryPlanAction(
                    primaryNode.Actions,
                    EVulkanPrimaryPlanAction.EndRendering))
                EndActiveRenderPass(ref recordingState);

            VulkanPrimaryOperationRecordingInfo recordingInfo = new(
                primaryNode.Actions,
                operationIndex,
                passIndex);
            return operation.RecordPrimary(
                this,
                ref recordingState,
                in recordingInfo);
        }
        finally
        {
            operationProfileScope?.Dispose();
        }
    }

    private void HandlePrimaryOperationRecordingFailure(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        FrameOp operation,
        Exception exception)
    {
        RecordDroppedPrimaryOperation(
            ref recordingState,
            operation,
            countIndirectCompute: true);
        recordingState.Metrics.FirstFailure ??=
            CaptureFrameOpFailure(operation, exception);

        EndActiveRenderPass(ref recordingState);
        if (recordingState.RenderPassLabelActive)
        {
            CmdEndLabel(recordingState.CommandBuffer);
            recordingState.RenderPassLabelActive = false;
        }

        string operationContext = BuildFrameOpFailureContext(operation);
        Debug.VulkanEvery(
            $"Vulkan.FrameOpError.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] Frame op recording failed for {0}: {1}: {2}{3}{4}",
            GetFrameOpDiagnosticName(operation),
            exception.GetType().Name,
            exception.Message,
            operationContext,
            exception.StackTrace is { Length: > 0 }
                ? Environment.NewLine + exception.StackTrace
                : string.Empty);
    }

    private static void RecordDroppedPrimaryOperation(
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        FrameOp operation,
        bool countIndirectCompute = false)
    {
        recordingState.Metrics.DroppedFrameOps++;
        if (operation is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp)
            recordingState.Metrics.DroppedDrawOps++;
        if (operation is ComputeDispatchOp ||
            countIndirectCompute && operation is ComputeDispatchIndirectOp)
            recordingState.Metrics.DroppedComputeOps++;
    }

    private static bool HasPrimaryPlanAction(
        EVulkanPrimaryPlanAction actions,
        EVulkanPrimaryPlanAction action)
        => (actions & action) != 0;
}
