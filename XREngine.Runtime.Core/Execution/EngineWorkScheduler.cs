using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Process-wide owner of persistent general, job-auxiliary, and render-critical
/// execution domains. Backends consume these domains through focused host
/// capabilities and do not construct another general worker pool.
/// </summary>
public sealed class EngineWorkScheduler : IDisposable
{
    private readonly EngineGeneralWorkDomain _generalDomain;
    private readonly EngineJobAuxiliaryWorkDomain _jobAuxiliaryDomain;
    private int _shutdownState;

    public EngineWorkScheduler(
        EngineExecutionTopology topology,
        int? generalQueueLimit = null,
        int? generalQueueWarningThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        Topology = topology;

        GeneralJobs = new JobManager(
            topology.GeneralWorkerThreadCount,
            generalQueueLimit,
            generalQueueWarningThreshold,
            topology.Request.GeneralWorkerThreadCap,
            createWorkerDomains: false);
        _generalDomain = new EngineGeneralWorkDomain(GeneralJobs, topology.GeneralWorkerThreadCount);
        _jobAuxiliaryDomain = new EngineJobAuxiliaryWorkDomain(GeneralJobs);

        try
        {
            GeneralJobs.AttachGeneralDomain(_generalDomain);
            GeneralJobs.AttachAuxiliaryDomain(_jobAuxiliaryDomain);
            Render = new RenderWorkDomain(
                topology.RenderWorkerThreadCount,
                topology.RenderWorkerQos);
        }
        catch
        {
            GeneralJobs.Shutdown(waitForWorkers: true);
            throw;
        }
    }

    public EngineExecutionTopology Topology { get; }
    public JobManager GeneralJobs { get; }
    public RenderWorkDomain Render { get; }

    public EngineWorkSchedulerMetrics Metrics => new(
        _generalDomain.WorkerCount,
        _generalDomain.DispatchCount,
        _generalDomain.WakeCount,
        _generalDomain.ThrottledDispatchCount,
        _generalDomain.ThrottleWaitTicks,
        _jobAuxiliaryDomain.GetMetrics(),
        Render.Metrics);

    public bool Shutdown(bool waitForWorkers = true)
        => Shutdown(waitForWorkers, RenderWorkDomain.FatalBatchWait);

    internal bool Shutdown(bool waitForWorkers, TimeSpan timeout)
    {
        Interlocked.Exchange(ref _shutdownState, 1);
        Render.Shutdown(waitForWorkers: false);
        GeneralJobs.Shutdown(waitForWorkers: false);
        if (!waitForWorkers)
            return false;

        long deadline = CreateDeadline(timeout);
        bool renderStopped = Render.Shutdown(
            waitForWorkers: true,
            GetRemaining(deadline));
        bool generalStopped = GeneralJobs.Shutdown(
            waitForWorkers: true,
            GetRemaining(deadline));
        return renderStopped && generalStopped;
    }

    /// <summary>
    /// Performs a bounded clean shutdown of every scheduler execution domain.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// A domain remained live at the lifecycle bound. Callers must retain all
    /// scheduler-dependent state and retry or abandon the process.
    /// </exception>
    public void Dispose()
    {
        if (!Shutdown(waitForWorkers: true))
        {
            throw new TimeoutException(
                "Engine scheduler disposal timed out with live execution work. " +
                "Scheduler-dependent state must remain alive until a later clean shutdown.");
        }
    }

    private static TimeSpan GetRemaining(long deadline)
    {
        long ticks = deadline - Stopwatch.GetTimestamp();
        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }

    private static long CreateDeadline(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return Stopwatch.GetTimestamp();

        double timeoutTicks = timeout.TotalSeconds * Stopwatch.Frequency;
        long boundedTicks = timeoutTicks >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1L, (long)timeoutTicks);
        long now = Stopwatch.GetTimestamp();
        return boundedTicks >= long.MaxValue - now ? long.MaxValue : now + boundedTicks;
    }
}
