namespace XREngine.Rendering.Compute;

/// <summary>
/// Compute operations required by the batched physics-chain pipeline.
/// </summary>
public readonly record struct PhysicsChainComputeCapabilities(
    bool SupportsComputeDispatch,
    bool SupportsShaderStorageBarriers,
    bool SupportsGpuBufferCopies,
    bool SupportsAsyncReadback,
    bool SupportsIndirectDispatch = false,
    bool SupportsSubgroupArithmetic = false)
{
    public bool SupportsRequiredPipeline
        => SupportsComputeDispatch
        && SupportsShaderStorageBarriers
        && SupportsGpuBufferCopies
        && SupportsAsyncReadback
        && SupportsIndirectDispatch;
}
