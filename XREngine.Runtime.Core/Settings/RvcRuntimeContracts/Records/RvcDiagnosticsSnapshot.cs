namespace XREngine;

public readonly record struct RvcDiagnosticsSnapshot(
    RvcPipelineResolution Resolution,
    RvcViewSetDiagnostics ViewSet,
    RvcFrameCounters Counters,
    ERvcDebugViewMode DebugViewMode,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public static RvcDiagnosticsSnapshot FromPlan(
        in RvcPipelinePlan plan,
        in RvcViewSetDiagnostics viewSet,
        in RvcFrameCounters counters)
        => new(
            plan.Resolution,
            viewSet,
            counters,
            plan.Settings.DebugViewMode,
            plan.Resolution.FallbackReason,
            plan.Resolution.Diagnostic);
}
