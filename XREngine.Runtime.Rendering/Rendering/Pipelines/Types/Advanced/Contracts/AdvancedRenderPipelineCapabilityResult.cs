namespace XREngine.Rendering;

/// <summary>
/// Structured result of validating one backend snapshot against the advanced pipeline floor.
/// </summary>
public readonly record struct AdvancedRenderPipelineCapabilityResult(
    AdvancedRenderPipelineCapabilities Capabilities,
    bool IsSupported,
    EAdvancedRenderPipelineRejectionReason RejectionReason)
{
    /// <summary>
    /// Human-readable explanation paired with the machine-readable rejection reason.
    /// </summary>
    public string Diagnostic
        => RejectionReason switch
        {
            EAdvancedRenderPipelineRejectionReason.None =>
                $"Advanced render pipeline requirements are available ({Capabilities.DescribeSelectedEncodings()}).",
            EAdvancedRenderPipelineRejectionReason.RendererUnavailable =>
                "No active renderer is available for advanced pipeline capability evaluation.",
            EAdvancedRenderPipelineRejectionReason.UnsupportedBackend =>
                "The active renderer does not expose the advanced pipeline capability contract.",
            EAdvancedRenderPipelineRejectionReason.MissingIntegerRenderTargets =>
                "The advanced pipeline requires an integer visibility render target.",
            EAdvancedRenderPipelineRejectionReason.MissingComputeShaders =>
                "The advanced pipeline requires compute shader dispatch.",
            EAdvancedRenderPipelineRejectionReason.MissingStorageBuffers =>
                "The advanced pipeline requires shader storage buffers.",
            EAdvancedRenderPipelineRejectionReason.MissingIndirectSubmission =>
                "The advanced pipeline requires indirect geometry submission.",
            EAdvancedRenderPipelineRejectionReason.MissingTextureIndirection =>
                "The advanced pipeline requires a GPU-addressable texture indirection encoding.",
            EAdvancedRenderPipelineRejectionReason.MissingSynchronization =>
                "The advanced pipeline requires explicit image and buffer synchronization.",
            EAdvancedRenderPipelineRejectionReason.MissingFrameSlotStorage =>
                "The advanced pipeline requires current and previous frame-slot storage.",
            EAdvancedRenderPipelineRejectionReason.MissingStereoArrayResources =>
                "Stereo advanced rendering requires layered texture-array resources.",
            EAdvancedRenderPipelineRejectionReason.MissingShaderFamily =>
                "The active backend does not provide the complete visibility-buffer shader family.",
            _ => "The advanced pipeline capability result is invalid.",
        };
}
