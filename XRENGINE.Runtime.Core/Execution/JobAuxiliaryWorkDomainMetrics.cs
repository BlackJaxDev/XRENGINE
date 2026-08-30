namespace XREngine.Execution;

/// <summary>
/// Snapshot of scheduler-owned deferred-admission and remote-dispatch lanes.
/// </summary>
public readonly record struct JobAuxiliaryWorkDomainMetrics(
    int WorkerCount,
    int RunningWorkerCount,
    long DeferredDispatchCount,
    long DeferredWakeCount,
    long RemoteDispatchCount,
    long RemoteWakeCount);
