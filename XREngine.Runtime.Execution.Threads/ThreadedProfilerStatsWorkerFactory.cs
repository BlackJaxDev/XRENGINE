namespace XREngine.Execution;

/// <summary>Creates native profiler statistics workers.</summary>
internal sealed class ThreadedProfilerStatsWorkerFactory : IProfilerStatsWorkerFactory
{
    public IProfilerStatsWorker Create(Func<int> runCycle, Action<Exception> reportException)
        => new ThreadedProfilerStatsWorker(runCycle, reportException);
}
