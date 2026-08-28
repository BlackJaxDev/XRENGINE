namespace XREngine.Rendering.Commands;

/// <summary>
/// Ordered draw exception retained outside the compact resident stream.
/// </summary>
public readonly record struct BackendReadyOrderedExceptionRecord(
    AdvancedGpuHandle DrawHandle,
    uint ViewId,
    int PassIndex,
    ulong OrderKey,
    uint ReasonFlags,
    EAdvancedCanonicalCompatibilityReason CompatibilityReason =
        EAdvancedCanonicalCompatibilityReason.None);
