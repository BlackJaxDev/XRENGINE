using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Planner-only preparation and operation-publication services for program wrappers.</summary>
internal sealed class VulkanProgramPlannerPort(VulkanFramePlanner framePlanner)
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
        FrameOpContext? context = framePlanner.ResourcePublications.GetPublishedGeneration().State.LastActiveFrameOpContext;
        return context is not { } active ? 0 : active.OutputTargetIdentity != 0 ? active.OutputTargetIdentity : active.ViewportIdentity;
    }

    internal FrameOpContext CaptureFrameOpContext()
        => framePlanner.ResourcePublications.GetPublishedGeneration().State.LastActiveFrameOpContext ?? default;

    internal void DispatchCompute(VkRenderProgram program, int x, int y, int z)
    {
        if (!program.Link(program.Data.AllowAsyncBackendCompile))
            return;
        FrameOpContext frameContext = framePlanner.ResourcePublications.GetPublishedGeneration().State.LastActiveFrameOpContext ?? default;
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;
        ComputeDispatchSnapshot snapshot = program.CaptureComputeSnapshot();
        if (!program.ValidateComputeSnapshot(snapshot, out _))
            return;
        try
        {
            if (program.GetOrCreateComputePipeline(passIndex, frameContext.PassMetadata).Handle == 0)
                return;
        }
        catch { return; }
        VulkanFrameOperationQueue queue = framePlanner.Operations;
        using (queue.SyncRoot.EnterScope())
            queue.Pending.Add(ComputeDispatchOp.Rent(passIndex, program, checked((uint)Math.Max(x, 1)), checked((uint)Math.Max(y, 1)), checked((uint)Math.Max(z, 1)), snapshot, frameContext));
    }

    internal void EnqueueTransformFeedback(VkTransformFeedback transformFeedback, EXRTransformFeedbackOperation operation, XRDataBuffer? counterBuffer, ulong feedbackBufferOffset, ulong? feedbackBufferSize, ulong counterBufferOffset, uint counterOffset, uint vertexStride, uint instanceCount, uint firstInstance)
    {
        ArgumentNullException.ThrowIfNull(transformFeedback);
        FrameOpContext frameContext = framePlanner.ResourcePublications.GetPublishedGeneration().State.LastActiveFrameOpContext ?? default;
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;
        VulkanFrameOperationQueue queue = framePlanner.Operations;
        using (queue.SyncRoot.EnterScope())
            queue.Pending.Add(new TransformFeedbackOp(passIndex, frameContext.OutputFrameBuffer, transformFeedback, operation, counterBuffer, feedbackBufferOffset, feedbackBufferSize, counterBufferOffset, counterOffset, vertexStride, instanceCount, firstInstance, frameContext));
    }
}
