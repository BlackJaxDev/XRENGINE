using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Planner-only preparation and operation-publication services for program wrappers.</summary>
internal sealed class VulkanProgramPlannerPort(
    VulkanFramePlanner framePlanner,
    VulkanCommandThreadWorkspace commandWorkspace)
{
    /// <summary>
    /// The planning authority owns the callback registration.  The program
    /// wrapper retains neither this port nor a planner callback.
    /// </summary>
    internal void Attach(VkRenderProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        program.Data.DispatchComputeRequested += (x, y, z, textures)
            => program.HandlePlannerDispatch(this, x, y, z, textures);
    }

    internal void TrackBufferBinding(XRDataBuffer buffer)
    {
        if (buffer is not null)
            framePlanner.ResourcePublications.TrackBufferBinding(buffer);
    }

    internal int ResolveDescriptorViewFamilyIdentity()
    {
        FrameOpContext? context = GetCurrentGeneration().State.LastActiveFrameOpContext;
        return context is not { } active ? 0 : active.OutputTargetIdentity != 0 ? active.OutputTargetIdentity : active.ViewportIdentity;
    }

    internal FrameOpContext CaptureFrameOpContext()
        => GetCurrentGeneration().State.LastActiveFrameOpContext ?? default;

    internal void DispatchCompute(VkRenderProgram program, int x, int y, int z)
    {
        if (!program.Link(program.Data.AllowAsyncBackendCompile))
            return;
        FrameOpContext frameContext = GetCurrentGeneration().State.LastActiveFrameOpContext ?? default;
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;
        ComputeDispatchSnapshot snapshot = program.CaptureComputeSnapshot();
        if (!program.ValidateComputeSnapshot(snapshot, out _))
            return;
        // Native compute pipeline readiness is resolved by the frame-plan
        // preparation authority before sealing. Do not erase this dispatch
        // while its asynchronously compiled pipeline is pending: the next
        // readiness retry must observe the exact program/snapshot request.
        VulkanFrameOperationQueue queue = framePlanner.Operations;
        ComputeDispatchOp operation = ComputeDispatchOp.Rent(
            passIndex,
            program,
            checked((uint)Math.Max(x, 1)),
            checked((uint)Math.Max(y, 1)),
            checked((uint)Math.Max(z, 1)),
            snapshot,
            frameContext);
        queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(operation, passIndex));
    }

    internal void EnqueueTransformFeedback(VkTransformFeedback transformFeedback, EXRTransformFeedbackOperation operation, XRDataBuffer? counterBuffer, ulong feedbackBufferOffset, ulong? feedbackBufferSize, ulong counterBufferOffset, uint counterOffset, uint vertexStride, uint instanceCount, uint firstInstance)
    {
        ArgumentNullException.ThrowIfNull(transformFeedback);
        FrameOpContext frameContext = GetCurrentGeneration().State.LastActiveFrameOpContext ?? default;
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;
        VulkanFrameOperationQueue queue = framePlanner.Operations;
        TransformFeedbackOp frameOperation = new(
            passIndex,
            frameContext.OutputFrameBuffer,
            transformFeedback,
            operation,
            counterBuffer,
            feedbackBufferOffset,
            feedbackBufferSize,
            counterBufferOffset,
            counterOffset,
            vertexStride,
            instanceCount,
            firstInstance,
            frameContext);
        queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(frameOperation, passIndex));
    }

    /// <summary>
    /// Selects the immutable planner generation installed for the current
    /// command scope. Wrapper callbacks can run while a nested render-pipeline
    /// resource scope is active, so reading only the globally published
    /// generation can attach a stale planner key to a newly enqueued operation.
    /// </summary>
    private ResourcePlannerRuntimeGeneration GetCurrentGeneration()
    {
        if (commandWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) &&
            context.ResourcePlannerRuntimeGeneration is { } scopedGeneration)
        {
            return scopedGeneration;
        }

        return framePlanner.ResourcePublications.GetPublishedGeneration();
    }
}
