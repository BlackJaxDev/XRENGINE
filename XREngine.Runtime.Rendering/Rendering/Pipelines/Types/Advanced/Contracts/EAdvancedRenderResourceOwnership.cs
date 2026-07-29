namespace XREngine.Rendering;

/// <summary>
/// Logical ownership classes used by advanced desktop frame resources.
/// </summary>
public enum EAdvancedRenderResourceOwnership
{
    /// <summary>
    /// Allocated and retired with a pipeline resource generation.
    /// </summary>
    PipelinePersistent = 0,

    /// <summary>
    /// Replicated per in-flight frame slot and writable only by that slot.
    /// </summary>
    FrameSlotTransient,

    /// <summary>
    /// Pipeline-owned history whose current and previous identities rotate at a frame boundary.
    /// </summary>
    TemporalHistory,

    /// <summary>
    /// Scene- or feature-owned data imported into the pipeline without transferring ownership.
    /// </summary>
    Imported,

    /// <summary>
    /// Presentation or caller-owned output acquired and released at an explicit boundary.
    /// </summary>
    External,
}
