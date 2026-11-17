namespace XREngine.Rendering;

/// <summary>
/// Why the shared Advanced preparation returned a publication without GPU resources for a frame
/// (<c>GpuResourcesPublished = false</c>). Each value is one deferral site in
/// <see cref="AdvancedPreparationExtractor"/> or <see cref="AdvancedSharedPreparationService"/>.
/// </summary>
public enum EAdvancedPreparationDeferral
{
    /// <summary>Not deferred.</summary>
    None,
    /// <summary>The active frame package has no canonical publication from this world GPU scene.</summary>
    PackageWithoutCanonicalPublication,
    /// <summary>The world frame already prepared another scene sequence than the package requests.</summary>
    PackageSequenceMismatch,
    /// <summary>The package's publication sequence is no longer available.</summary>
    PackagePublicationRetired,
    /// <summary>The exact canonical scene publication snapshot is unavailable.</summary>
    CanonicalSnapshotUnavailable,
    /// <summary>The frame-upload arena slot is still owned by an earlier frame.</summary>
    FrameUploadSlotBusy,
    /// <summary>No GPU deformation output slot is reusable (see the deformation slot reasons).</summary>
    DeformationOutputSlot,
    /// <summary>The deformed-vertex arena slot is still owned by an earlier frame, or it must grow first.</summary>
    DeformedArenaSlotBusy,
    /// <summary>The deformation frame could not open after the slot was chosen (static generation or output growth).</summary>
    DeformationFrameOpen,
    /// <summary>The covered draw bounds output page is unavailable.</summary>
    BoundsPageUnavailable,
    /// <summary>Canonical GPU deformation mesh preparation is still in progress.</summary>
    DeformationMeshPreparationPending,
    /// <summary>The deformation output arena must grow before the scene can be prepared.</summary>
    DeformedArenaGrowth,
    /// <summary>The covered draw palette or authored envelope does not match its retained bounds page.</summary>
    BoundsPageMismatch,
    /// <summary>
    /// A later consumer of the same world frame received the frame's earlier deferred publication.
    /// This is not a new deferral; it shows how many consumers a deferral affected.
    /// </summary>
    DeferredWorldFrameReused,
    /// <summary>Number of values.</summary>
    Count,
}
