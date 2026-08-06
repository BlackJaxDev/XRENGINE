namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private PreparedCommandChainEncodingScope EnterPreparedCommandChainEncodingScope()
        => new(this);

    private VulkanPreparedWorkerPlannerStamp CapturePreparedWorkerPlannerStamp()
    {
        ResourcePlannerRuntimeState state = CaptureResourcePlannerRuntimeState();
        return new(
            state.ResourcePlanner,
            state.ResourceAllocator,
            state.BarrierPlanner,
            state.CompiledRenderGraph,
            state.ResourcePlannerSignature,
            state.ResourceAllocationSignature,
            state.ResourcePlannerRevision,
            state.FailedResourcePlannerSignature,
            state.FailedResourceAllocationSignature,
            state.FailedResourceAllocationTimestamp,
            state.HasResourcePlannerFastPathKey,
            state.HasBarrierPlanFastPathKey,
            state.LastActiveFrameOpContext);
    }

}
