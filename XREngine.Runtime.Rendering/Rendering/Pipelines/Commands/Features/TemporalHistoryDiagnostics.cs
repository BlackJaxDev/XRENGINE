namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Published temporal-history generations for one pipeline instance. Seeded
/// generations identify accepted history; reset generations survive the reset frame.
/// </summary>
public readonly record struct TemporalHistoryDiagnostics(
    int PipelineInstanceId,
    uint Width,
    uint Height,
    EVrTemporalHistoryPolicy IsolationPolicy,
    bool HistoryReady,
    bool LeftEyeHistoryReady,
    bool RightEyeHistoryReady,
    ulong ProfileGeneration,
    ulong LeftEyeResetGeneration,
    ulong RightEyeResetGeneration,
    ulong LeftEyeSeededGeneration,
    ulong RightEyeSeededGeneration,
    bool HistoryExposureReady);
