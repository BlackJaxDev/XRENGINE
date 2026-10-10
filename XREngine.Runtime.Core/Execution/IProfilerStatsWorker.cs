namespace XREngine.Execution;

/// <summary>Controls the lifetime of one profiler statistics worker.</summary>
/// <remarks>After Start succeeds, request stop and confirm exit before Dispose. Disposal does not stop or join a running worker.</remarks>
internal interface IProfilerStatsWorker : IDisposable
{
    /// <summary>Reports whether the worker thread is alive.</summary>
    bool IsAlive { get; }
    /// <summary>Starts the worker and its cycle callbacks.</summary>
    void Start();
    /// <summary>Requests cancellation without waiting for a callback or joining the thread.</summary>
    void RequestStop();
    /// <summary>Returns only after the worker thread and all its callbacks have exited.</summary>
    void WaitForExit();
}
