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
    bool SupportsSubgroupArithmetic = false,
    bool SupportsSubmissionFences = true,
    bool SupportsZeroReadbackPublication = true)
{
    /// <summary>
    /// Operations required for simulation, GPU-authored dispatch, palettes, and bounds.
    /// Host readback is deliberately excluded.
    /// </summary>
    public bool SupportsCorePipeline
        => SupportsComputeDispatch
        && SupportsShaderStorageBarriers
        && SupportsGpuBufferCopies
        && SupportsIndirectDispatch
        && SupportsZeroReadbackPublication;

    /// <summary>Operations required in addition to the core path for CPU mirroring.</summary>
    public bool SupportsReadbackPipeline
        => SupportsCorePipeline
        && SupportsSubmissionFences
        && SupportsAsyncReadback;

    /// <summary>
    /// Compatibility alias for callers that require every feature, including readback.
    /// </summary>
    public bool SupportsRequiredPipeline
        => SupportsReadbackPipeline;
}
