namespace XREngine.Rendering;

/// <summary>On-demand timing and state snapshot for the latest Advanced family preparation generation.</summary>
public readonly record struct AdvancedVisibilityPreparationDiagnosticsSnapshot(
    string State,
    string Reason,
    int AttemptCount,
    double WallMilliseconds,
    double PreparationCpuMilliseconds,
    double SourceCompilationMilliseconds,
    double ProgramLinkMilliseconds,
    double NativePipelineMilliseconds,
    long ForegroundJoinCount,
    double ForegroundJoinMilliseconds,
    long AdmissionPollCount,
    double AdmissionPollMilliseconds);