namespace XREngine.Execution;

/// <summary>Creates a profiler worker for a host-owned statistics cycle.</summary>
internal interface IProfilerStatsWorkerFactory
{
    /// <summary>Creates an unstarted worker. A cycle returns zero for no delay or a positive idle-sleep duration in milliseconds.</summary>
    /// <remarks>The worker reports cycle and sleep exceptions to reportException, then continues. An exception from reportException escapes the worker.</remarks>
    IProfilerStatsWorker Create(Func<int> runCycle, Action<Exception> reportException);
}
