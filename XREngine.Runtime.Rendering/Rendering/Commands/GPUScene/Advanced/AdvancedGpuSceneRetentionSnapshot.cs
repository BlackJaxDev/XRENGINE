namespace XREngine.Rendering.Commands;

/// <summary>
/// Point-in-time diagnostics for publication-ring retention. This is intended
/// for lifecycle validation and does not expose mutable ring storage.
/// </summary>
public readonly record struct AdvancedGpuSceneRetentionSnapshot(
    int RetainedPublicationCount,
    ulong OldestPublicationSequence,
    uint OldestPackagePinCount,
    uint OldestGpuPinCount,
    uint TotalPackagePinCount,
    uint TotalGpuPinCount);
