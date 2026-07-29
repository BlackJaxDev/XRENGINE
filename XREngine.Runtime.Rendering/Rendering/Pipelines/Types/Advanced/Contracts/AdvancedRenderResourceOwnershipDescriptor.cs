using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Runtime lifetime and mutation rules for one advanced resource ownership class.
/// </summary>
public readonly record struct AdvancedRenderResourceOwnershipDescriptor(
    EAdvancedRenderResourceOwnership Ownership,
    RenderResourceLifetime RuntimeLifetime,
    bool PipelineAllocates,
    bool PipelineDisposes,
    bool ReplicatedPerFrameSlot,
    bool RotatesHistory,
    bool RequiresExplicitBinding,
    bool RequiresOwnerSynchronization);
