namespace XREngine.Rendering.Commands;

/// <summary>
/// Dirty range emitted by a canonical resident owner table.
/// </summary>
public readonly record struct BackendReadyCanonicalDirtyOwnerRange(
    EBackendReadyCanonicalOwner Owner,
    AdvancedGpuDirtyRange Range,
    ulong PublicationGeneration);
