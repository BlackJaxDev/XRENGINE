namespace XREngine.Rendering;

/// <summary>
/// Machine-readable first rejection reason for an advanced pipeline request.
/// </summary>
public enum EAdvancedRenderPipelineRejectionReason
{
    None = 0,
    RendererUnavailable,
    UnsupportedBackend,
    MissingIntegerRenderTargets,
    MissingComputeShaders,
    MissingStorageBuffers,
    MissingIndirectSubmission,
    MissingTextureIndirection,
    MissingSynchronization,
    MissingFrameSlotStorage,
    MissingStereoArrayResources,
    MissingShaderFamily,
}
