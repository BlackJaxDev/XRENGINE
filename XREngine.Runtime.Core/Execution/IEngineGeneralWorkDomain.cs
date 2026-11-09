namespace XREngine.Execution;

/// <summary>Controls one domain that drains general jobs.</summary>
/// <remarks>
/// The manager owns the domain before it calls Start. Shutdown may be called
/// repeatedly. A successful waiting shutdown means no owned worker remains;
/// incomplete work must retain the resources that its workers can still use.
/// </remarks>
public interface IEngineGeneralWorkDomain
{
    /// <summary>Gets the number of lanes that may drain work now.</summary>
    int WorkerCount { get; }

    /// <summary>Gets the number of lane threads that the domain owns.</summary>
    int ThreadCount { get; }
    long DispatchCount { get; }
    long WakeCount { get; }
    long ThrottledDispatchCount { get; }
    long ThrottleWaitTicks { get; }

    void Start();
    void NotifyWorkAvailable();

    /// <summary>
    /// Sets how many lanes may drain work, between 1 and <see cref="ThreadCount"/>. A lane above the
    /// limit finishes its current job and then parks; a raised limit wakes parked lanes. No thread
    /// starts or stops, and no queued work is dropped.
    /// </summary>
    void SetActiveWorkerLimit(int limit);
    bool Shutdown(bool waitForWorkers);
    bool Shutdown(bool waitForWorkers, TimeSpan timeout);
}
