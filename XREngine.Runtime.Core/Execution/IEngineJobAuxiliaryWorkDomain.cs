namespace XREngine.Execution;

/// <summary>Controls the deferred-admission and remote-dispatch lanes.</summary>
/// <remarks>
/// The manager owns the domain before it calls Start. Shutdown may be called
/// repeatedly. A successful waiting shutdown means no owned worker remains;
/// incomplete work must retain the resources that its workers can still use.
/// </remarks>
public interface IEngineJobAuxiliaryWorkDomain
{
    int WorkerCount { get; }
    int RunningWorkerCount { get; }
    long DeferredDispatchCount { get; }
    long DeferredWakeCount { get; }
    long RemoteDispatchCount { get; }
    long RemoteWakeCount { get; }

    void Start();
    void NotifyDeferredWorkAvailable();
    void NotifyRemoteWorkAvailable();
    JobAuxiliaryWorkDomainMetrics GetMetrics();
    bool Shutdown(bool waitForWorkers);
    bool Shutdown(bool waitForWorkers, TimeSpan timeout);
}
