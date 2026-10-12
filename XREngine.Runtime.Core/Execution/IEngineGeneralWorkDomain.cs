namespace XREngine.Execution;

/// <summary>Controls one domain that drains general jobs.</summary>
/// <remarks>
/// The manager owns the domain before it calls Start. Shutdown may be called
/// repeatedly. A successful waiting shutdown means no owned worker remains;
/// incomplete work must retain the resources that its workers can still use.
/// </remarks>
public interface IEngineGeneralWorkDomain
{
    int WorkerCount { get; }
    long DispatchCount { get; }
    long WakeCount { get; }
    long ThrottledDispatchCount { get; }
    long ThrottleWaitTicks { get; }

    void Start();
    void NotifyWorkAvailable();
    bool Shutdown(bool waitForWorkers);
    bool Shutdown(bool waitForWorkers, TimeSpan timeout);
}
