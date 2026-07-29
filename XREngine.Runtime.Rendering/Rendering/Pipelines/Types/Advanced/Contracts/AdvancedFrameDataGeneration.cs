namespace XREngine.Rendering;

/// <summary>
/// GPU-written or upload-published frame contents that remain behind stable bindings.
/// Changes here refresh data but never invalidate command topology.
/// </summary>
public readonly record struct AdvancedFrameDataGeneration(
    ulong Counts,
    ulong Visibility,
    ulong Transforms,
    ulong Materials);
