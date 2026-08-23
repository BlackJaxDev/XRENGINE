namespace XREngine.Execution;

/// <summary>
/// Process scheduler metrics grouped by execution domain.
/// </summary>
public readonly record struct EngineWorkSchedulerMetrics(
    int GeneralWorkerCount,
    long GeneralDispatchCount,
    long GeneralWakeCount,
    RenderWorkDomainMetrics Render);
