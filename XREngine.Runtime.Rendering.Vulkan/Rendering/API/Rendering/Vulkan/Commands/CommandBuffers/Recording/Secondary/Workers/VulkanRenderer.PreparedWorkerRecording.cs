namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private PreparedCommandChainEncodingScope EnterPreparedCommandChainEncodingScope()
        => new(this);

    private VulkanPreparedWorkerPlannerStamp CapturePreparedWorkerPlannerStamp()
        => new(
            _resourcePlanner,
            _resourceAllocator,
            _barrierPlanner,
            _compiledRenderGraph,
            _resourcePlannerSignature,
            _resourceAllocationSignature,
            _resourcePlannerRevision,
            _renderGraphRuntime.FailedPlannerSignature,
            _renderGraphRuntime.FailedAllocationSignature,
            _renderGraphRuntime.FailedAllocationTimestamp,
            _hasResourcePlannerFastPathKey,
            _hasBarrierPlanFastPathKey,
            _renderGraphRuntime.LastActiveFrameOpContext);

}
