using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    // Do not expose the executor until Final's image-access journal and the
    // explicit canonical-publication bootstrap pass native runtime validation.
    private static bool AdvancedQueueOverlapRuntimeValidated => false;

    /// <summary>
    /// Lowers one exact mono Advanced output to an explicitly owned four-primary
    /// execution. Other output families retain their independently validated path.
    /// </summary>
    private void PrepareAdvancedQueueOverlapRecording(scoped ref PrimaryCommandBufferRecordingState state)
    {
        EVulkanQueueOverlapMode mode = state.Policy.QueueOverlapMode;
        if (mode is EVulkanQueueOverlapMode.Auto or EVulkanQueueOverlapMode.GraphicsOnly)
            return;
        if (!AdvancedQueueOverlapRuntimeValidated)
            throw new NotSupportedException("The Advanced multi-queue executor is implemented but disabled pending canonical-publication, image-access, and native runtime validation. Select GraphicsOnly explicitly to run the validated path.");
        if (mode != EVulkanQueueOverlapMode.GraphicsCompute ||
            !state.Policy.AllowAdvancedQueueOverlap ||
            !DeviceContext.HasSecondaryGraphicsQueue ||
            DeviceContext.SecondaryGraphicsQueue.Handle == DeviceContext.GraphicsQueue.Handle ||
            !DeviceContext.QueueFamilies.GraphicsFamilySupportsCompute ||
            state.OpenXrTargetContext is not null)
            throw new NotSupportedException("GraphicsCompute requires a presentationless mono Advanced output and a distinct compute-capable queue in the graphics family. GraphicsComputeTransfer is not supported.");

        int ambient = -1, classification = -1, opaque = -1;
        int pipelineIdentity = 0;
        for (int i = 0; i < state.Ops.Length; i++)
        {
            if (state.Ops.GetHeader(i).OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
                continue;
            ref readonly VulkanAdvancedVisibilityOperationPayload payload = ref state.Ops.GetAdvancedVisibility(i);
            EAdvancedRenderStage stage = payload.Request.Stage;
            if (stage is not (EAdvancedRenderStage.AmbientOcclusion or EAdvancedRenderStage.WorkClassification or EAdvancedRenderStage.NativeOpaqueShading))
                continue;
            int identity = state.Ops.GetContext(i).PipelineIdentity;
            if (payload.State.ViewCount != 1 || payload.NativeComputeClosure is not { IsValid: true, ViewIndex: 0 } ||
                (pipelineIdentity != 0 && pipelineIdentity != identity))
                throw new NotSupportedException("GraphicsCompute supports exactly one mono Advanced native output per production submission.");
            pipelineIdentity = identity;
            ref int operation = ref (stage == EAdvancedRenderStage.AmbientOcclusion ? ref ambient :
                ref (stage == EAdvancedRenderStage.WorkClassification ? ref classification : ref opaque));
            if (operation >= 0)
                throw new NotSupportedException("GraphicsCompute cannot split multiple Advanced output cohorts in one submission.");
            operation = i;
        }
        if (ambient < 0 || classification <= ambient || opaque <= classification)
            throw new NotSupportedException("GraphicsCompute requires the Advanced GTAO, work-classification, and native-opaque stages in one exact frame.");

        VulkanAdvancedQueueOverlapSlot slot = PrepareAdvancedQueueOverlapSlot(state.FrameDataImageIndex, state.CommandBuffer);
        slot.AmbientOcclusionOperation = ambient;
        slot.ClassificationOperation = classification;
        slot.NativeOpaqueOperation = opaque;
        state.AdvancedQueueOverlap = slot;
        state.CommandBuffer = slot.Prefix;
    }

    private bool RecordAdvancedQueueOverlapOperations(scoped ref PrimaryCommandBufferRecordingState state)
    {
        if (state.AdvancedQueueOverlap is not { } slot)
            return RecordPrimaryOperations(ref state);

        if (!RecordPrimaryOperations(ref state, 0, slot.AmbientOcclusionOperation))
            return false;
        EndActiveRenderPass(ref state);
        // Both fork children consume these images. Establish their common layout
        // once, before Ready, instead of racing identical transitions on two queues.
        var closure = state.Ops.GetAdvancedVisibility(slot.ClassificationOperation).NativeComputeClosure
            ?? throw new VulkanPlanPreconditionException("The classification resource closure was lost before recording.");
        TransitionNativeInput(state.CommandBuffer, closure.Identity, 0);
        TransitionNativeInput(state.CommandBuffer, closure.Metadata, 0);
        EndAdvancedQueueOverlapSegment(ref state);

        BeginAdvancedQueueOverlapSegment(ref state, slot.Classification, slot.Prefix, "Advanced.ClassificationQueue");
        if (!RecordPrimaryOperations(ref state, slot.ClassificationOperation, slot.ClassificationOperation + 1))
            return false;
        EndAdvancedQueueOverlapSegment(ref state);

        BeginAdvancedQueueOverlapSegment(ref state, slot.Independent, slot.Prefix, "Advanced.IndependentGraphics");
        // NativeOpaque performs the final split after its independent preparation.
        return RecordPrimaryOperations(ref state, slot.AmbientOcclusionOperation, skipOperation: slot.ClassificationOperation);
    }

    private void BeginAdvancedQueueOverlapShade(scoped ref PrimaryCommandBufferRecordingState state)
    {
        if (state.AdvancedQueueOverlap is not { } slot)
            return;
        EndAdvancedQueueOverlapSegment(ref state);
        BeginAdvancedQueueOverlapSegment(ref state, slot.Final, slot.Independent, "Advanced.ShadeAndOutput");
    }

    private void EndAdvancedQueueOverlapSegment(scoped ref PrimaryCommandBufferRecordingState state)
    {
        EndActiveRenderPass(ref state);
        if (state.ActiveInlineQuery is not null)
            throw new VulkanPlanPreconditionException("An inline query cannot span the Advanced queue boundary.");
        if (state.PassIndexLabelActive)
        {
            CmdEndLabel(state.CommandBuffer);
            state.PassIndexLabelActive = false;
        }
        CmdEndLabel(state.CommandBuffer);
        if (!EndPrimaryCommandBuffer(ref state))
            throw new VulkanPlanPreconditionException(state.RecordingDeferredReason);
        if (state.FramePlan is { } plan)
            RegisterRecordedAdvancedVisibilityBanks(state.CommandBuffer, plan);
    }

    private void BeginAdvancedQueueOverlapSegment(scoped ref PrimaryCommandBufferRecordingState state,
        CommandBuffer commandBuffer, CommandBuffer predecessor, string label)
    {
        CleanupPrimaryCommandRecording(ref state);
        state.ActiveResourcePlannerScopeSet = false;
        state.ActivePipelineOverrideScopeSet = false;
        state.HasActiveContext = false;
        state.HasPlannerContext = false;
        state.ActivePassIndex = int.MinValue;
        state.ActiveSchedulingIdentity = int.MinValue;
        state.RenderPassLabelActive = false;
        state.RenderScope.Deactivate();
        state.CommandBuffer = commandBuffer;
        Result resetResult = ResetVulkanCommandBufferTracked(commandBuffer);
        if (resetResult != Result.Success)
            throw new VulkanPlanPreconditionException($"The Advanced queue command buffer could not be reset ({resetResult}).");
        ResetSubmissionMarkersForCommandBuffer(commandBuffer);
        BeginRecording(VulkanApi, DeviceContext.StateMachine, commandBuffer, "vkBeginCommandBuffer.AdvancedQueueOverlap");
        state.LaneContext = LaneRecordingContexts.BeginContext(EVulkanAcceptedFrameLane.MainScene,
            (int)state.FrameDataImageIndex, commandBuffer, CommandBuffers.ResolveRecordingGeneration(commandBuffer));
        SeedFreshExecutionImageLayoutState(commandBuffer, predecessor);
        CmdBeginLabel(commandBuffer, label);
    }
}
