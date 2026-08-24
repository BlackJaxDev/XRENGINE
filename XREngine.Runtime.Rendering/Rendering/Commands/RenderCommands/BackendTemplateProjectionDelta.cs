namespace XREngine.Rendering.Commands;

/// <summary>
/// Structural canonical delta consumed by a backend-specific template cache.
/// Data-only and view-only changes intentionally do not enter this stream.
/// </summary>
public readonly record struct BackendTemplateProjectionDelta(
    EBackendTemplateProjectionDeltaKind Kind,
    EBackendTemplateMutationDomain Domain,
    EBackendReadyCanonicalOwner Owner,
    AdvancedGpuHandle Handle,
    AdvancedGpuHandle PreviousHandle,
    ulong PublicationGeneration,
    uint PreviousDenseIndex,
    uint CurrentDenseIndex);
