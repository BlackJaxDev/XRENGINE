namespace XREngine.Rendering;

/// <summary>
/// Immutable view attribution copied from the exact frozen views admitted by
/// the selected pipeline instance's native preparation. It identifies a logical eye;
/// it does not claim that a GPU counter was read back for that eye.
/// </summary>
public readonly record struct AdvancedProfileEyeDiagnostic(
    ulong FrameId,
    int ResourceGeneration,
    ulong OutputId,
    uint ViewId,
    uint OutputLayer,
    ulong HistoryKey,
    uint ViewMaskLo,
    uint ViewMaskHi,
    EAdvancedViewRecordFlags Flags,
    int Width,
    int Height);
