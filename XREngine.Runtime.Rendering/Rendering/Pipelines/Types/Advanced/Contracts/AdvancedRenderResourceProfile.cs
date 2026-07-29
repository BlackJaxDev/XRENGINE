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
    /// First version of the advanced resource/state contract.
    /// </summary>
    public const uint CurrentContractVersion = 1u;

    /// <summary>
    /// Captures the inactive frame skeleton without speculatively reserving stage resources.
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
    /// Creates the structural resource-generation key after validating slot separation.
    /// </summary>
    public AdvancedRenderResourceGenerationKey ToGenerationKey()
    {
        AdvancedFrameSlotContract.ValidateSlotCount(FrameSlotCount);
        return new AdvancedRenderResourceGenerationKey(this);
    }
}
