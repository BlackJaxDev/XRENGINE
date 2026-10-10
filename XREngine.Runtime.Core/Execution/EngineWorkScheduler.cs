using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Process-wide owner of persistent general, job-auxiliary, and render-critical
/// execution domains. Backends consume these domains through focused host
/// capabilities and do not construct another general worker pool.
/// </summary>
public sealed class EngineWorkScheduler : IDisposable
{
    private readonly IEngineGeneralWorkDomain _generalDomain;
    private readonly IEngineJobAuxiliaryWorkDomain _jobAuxiliaryDomain;
    private int _shutdownState;

    public EngineWorkScheduler(
        EngineExecutionTopology topology,
        int? generalQueueLimit = null,
        int? generalQueueWarningThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        IEngineWorkerDomainFactory workerFactory = EngineWorkerDomainServices.GetRequiredFactory();
        Topology = topology;

        GeneralJobs = new JobManager(
            topology.GeneralWorkerThreadCount,
            generalQueueLimit,
            generalQueueWarningThreshold,
            topology.Request.GeneralWorkerThreadCap,
            createWorkerDomains: false);
        GeneralJobs.InitializeWorkerDomains(workerFactory, topology.GeneralWorkerThreadCount);
        _generalDomain = GeneralJobs.GeneralDomain;
        _jobAuxiliaryDomain = GeneralJobs.AuxiliaryDomain;
        try
        {
            Render = new RenderWorkDomain(
                topology.RenderWorkerThreadCount,
                topology.RenderWorkerQos);
        }
        catch (Exception startupError)
        {
            bool stopped;
            Exception? cleanupError;
            try
            {
                stopped = GeneralJobs.Shutdown(
                    waitForWorkers: true,
                    RenderWorkDomain.FatalBatchWait,
                    out cleanupError);
            }
            catch (Exception exception)
            {
                throw new AggregateException(startupError, exception);
            }

            if (!stopped)
            {
                cleanupError ??= new TimeoutException(
                    "Job worker cleanup did not finish within the scheduler shutdown bound.");
                throw new AggregateException(startupError, cleanupError);
            }

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
        bool stopped = Shutdown(waitForWorkers, timeout, out Exception? shutdownError);
        if (shutdownError is not null)
            throw shutdownError;
        return stopped;
    }

    private bool Shutdown(bool waitForWorkers, TimeSpan timeout, out Exception? shutdownError)
    {
        long deadline = CreateDeadline(timeout);
        List<Exception>? failures = null;
        Interlocked.Exchange(ref _shutdownState, 1);
        try
        {
            Render.Shutdown(waitForWorkers: false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            GeneralJobs.Shutdown(waitForWorkers: false, GetRemaining(deadline), out Exception? jobError);
            if (jobError is not null)
                (failures ??= new List<Exception>()).Add(jobError);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        if (!waitForWorkers)
        {
            shutdownError = failures is null ? null : new AggregateException(failures);
            return false;
        }

        bool renderStopped = false;
        try
        {
            renderStopped = Render.Shutdown(
                waitForWorkers: true,
                GetRemaining(deadline));
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        bool generalStopped = false;
        try
        {
            generalStopped = GeneralJobs.Shutdown(
                waitForWorkers: true,
                GetRemaining(deadline),
                out Exception? jobError);
            if (jobError is not null)
                (failures ??= new List<Exception>()).Add(jobError);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        shutdownError = failures is null ? null : new AggregateException(failures);
        return renderStopped && generalStopped && failures is null;
    }

    /// <summary>
    /// Performs a bounded clean shutdown of every scheduler execution domain.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// A domain remained live at the lifecycle bound. Callers must retain all
    /// scheduler-dependent state and retry or abandon the process.
    /// </exception>
    /// <exception cref="AggregateException">A domain reported a shutdown fault.</exception>
    public void Dispose()
    {
        if (!Shutdown(waitForWorkers: true, RenderWorkDomain.FatalBatchWait, out Exception? shutdownError))
        {
            if (shutdownError is not null)
                throw shutdownError;

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
