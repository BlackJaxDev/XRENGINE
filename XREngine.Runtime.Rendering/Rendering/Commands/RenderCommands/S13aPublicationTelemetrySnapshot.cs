namespace XREngine.Rendering.Commands;

/// <summary>
/// Cumulative S13a counters captured with independent atomic reads. Compare two
/// snapshots to observe a window; fields do not represent one atomic frame.
/// </summary>
public readonly record struct S13aPublicationTelemetrySnapshot(
    bool Enabled, long StopwatchFrequency, long SnapshotSequence, long CapturedTimestamp,
    long DirtyIdentityNotifications,
    long DirtyOtherNotifications, long DirtyAlreadySet, long ManualDirty,
    long QueueAdds, long QueueDuplicates, long QueueClean, long SwapCount,
    long SwapQueued, long SwapCallbacks, long SwapAuthorityYields,
    long MeshUpdateCalls, long MeshUpdateChanged, long MeshUpdateUnchanged,
    long MeshUpdateFailed,
    long MeshUpdateWaitTicks, long MeshUpdateBodyTicks, long MeshUpdateHeldTicks,
    long MeshUpdateMaterialTicks, long MeshUpdateRegistrationTicks, long MeshUpdateWriteTicks,
    long MeshUpdateAllocationBytes,
    long MeshUpdateSubmeshes, long MeshUpdateRegistrationAttempts,
    long MeshUpdateMetadataWrites, long MeshUpdateStateClassWrites,
    long MeshUpdateTransparencyWrites,
    long PublicationReused, long PublicationMissing, long PublicationExpired,
    long PublicationResourceMutation,
    long PublicationMaterialMutation, long PublicationCommandMutation,
    long PublicationTemporalMutation, long PublicationRegistrationRemoval);
