using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Immutable resource/state profile. Every field either affects allocation, binding shape,
/// frame-slot replication, or the backend encoding of that declared layout.
/// </summary>
public readonly record struct AdvancedRenderResourceProfile(
    uint ContractVersion,
    RenderPipelineResourceProfile Target,
    uint FrameSlotCount,
    EAdvancedVisibilityTargetEncoding VisibilityTargetEncoding,
    EAdvancedIndirectSubmissionMode IndirectSubmission,
    EAdvancedTextureIndirectionMode TextureIndirection,
    EAdvancedSynchronizationMode Synchronization,
    EAdvancedShaderFamily ShaderFamily,
    AdvancedRenderCapacityProfile Capacities)
{
    /// <summary>
    /// Document-05 resource contract, including shader-local surface reconstruction.
    /// </summary>
    public const uint CurrentContractVersion = 3u;

    /// <summary>
    /// Captures the inactive frame skeleton for tests of unsupported hosts.
    /// </summary>
    public static AdvancedRenderResourceProfile CreateInactive(
        in RenderPipelineResourceProfile target,
        in AdvancedRenderPipelineCapabilities capabilities)
        => new(
            CurrentContractVersion,
            target,
            AdvancedFrameSlotContract.DefaultSlotCount,
            capabilities.VisibilityTargetEncoding,
            capabilities.IndirectSubmission,
            capabilities.TextureIndirection,
            capabilities.Synchronization,
            capabilities.ShaderFamily,
            AdvancedRenderCapacityProfile.Inactive);

    /// <summary>
    /// Captures the declared visibility/depth/history/frame-slot layout.
    /// </summary>
    public static AdvancedRenderResourceProfile CreateVisibilityBuffer(
        in RenderPipelineResourceProfile target,
        in AdvancedRenderPipelineCapabilities capabilities)
        => new(
            CurrentContractVersion,
            target,
            AdvancedFrameSlotContract.DefaultSlotCount,
            capabilities.VisibilityTargetEncoding,
            capabilities.IndirectSubmission,
            capabilities.TextureIndirection,
            capabilities.Synchronization,
            capabilities.ShaderFamily,
            AdvancedRenderCapacityProfile.VisibilityBuffer);

    /// <summary>
    /// Captures visibility plus shader-local reconstruction diagnostics and counters.
    /// </summary>
    public static AdvancedRenderResourceProfile CreateAttributeReconstruction(
        in RenderPipelineResourceProfile target,
        in AdvancedRenderPipelineCapabilities capabilities)
        => new(
            CurrentContractVersion,
            target,
            AdvancedFrameSlotContract.DefaultSlotCount,
            capabilities.VisibilityTargetEncoding,
            capabilities.IndirectSubmission,
            capabilities.TextureIndirection,
            capabilities.Synchronization,
            capabilities.ShaderFamily,
            AdvancedRenderCapacityProfile.AttributeReconstruction);

    /// <summary>
    /// Creates the structural resource-generation key after validating slot separation.
    /// </summary>
    public AdvancedRenderResourceGenerationKey ToGenerationKey()
    {
        AdvancedFrameSlotContract.ValidateSlotCount(FrameSlotCount);
        return new AdvancedRenderResourceGenerationKey(this);
    }
}
