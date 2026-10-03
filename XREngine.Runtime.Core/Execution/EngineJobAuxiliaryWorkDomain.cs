using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Scheduler-owned lanes for general-job queue admission and remote dispatch.
/// These lanes may block independently without consuming a general or
/// render-critical worker.
/// </summary>
internal sealed class EngineJobAuxiliaryWorkDomain
{
    private static readonly TimeSpan WorkerJoinTimeout = TimeSpan.FromSeconds(2);
    private readonly JobManager _jobs;
    private readonly Thread _deferredEnqueueWorker;
    private readonly Thread _remoteDispatchWorker;
    private readonly SemaphoreSlim _deferredReadySignal = new(0, 1);
    private readonly SemaphoreSlim _remoteReadySignal = new(0, 1);
    private int _deferredSignalPending;
    private int _remoteSignalPending;
    private int _started;
    private int _shutdownState;
    private int _synchronizationDisposed;
    private long _deferredDispatchCount;
    private long _deferredWakeCount;
    private long _remoteDispatchCount;
    private long _remoteWakeCount;

    internal EngineJobAuxiliaryWorkDomain(JobManager jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _jobs = jobs;
        _deferredEnqueueWorker = new Thread(DeferredEnqueueWorkerLoop)
        {
            IsBackground = true,
            Name = "job-manager-deferred-enqueue",
        };
        _remoteDispatchWorker = new Thread(RemoteDispatchWorkerLoop)
        {
            IsBackground = true,
            Name = "job-manager-remote-dispatch",
        };
    }

    internal int WorkerCount => 2;
    internal int RunningWorkerCount
        => (_deferredEnqueueWorker.IsAlive ? 1 : 0) +
           (_remoteDispatchWorker.IsAlive ? 1 : 0);
    internal long DeferredDispatchCount => Interlocked.Read(ref _deferredDispatchCount);
    internal long DeferredWakeCount => Interlocked.Read(ref _deferredWakeCount);
    internal long RemoteDispatchCount => Interlocked.Read(ref _remoteDispatchCount);
    internal long RemoteWakeCount => Interlocked.Read(ref _remoteWakeCount);

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The job auxiliary work domain was already started.");

        try
        {
            _deferredEnqueueWorker.Start();
            _remoteDispatchWorker.Start();
        }
        catch (Exception exception)
        {
            if (!Shutdown(waitForWorkers: true))
            {
                Environment.FailFast(
                    "A job auxiliary scheduler thread failed to start and the partially started domain did not quiesce.",
                    exception);
            }

            throw;
        }
    }

    internal void NotifyDeferredWorkAvailable()
        => NotifyWorkAvailable(_deferredReadySignal, ref _deferredSignalPending);

    internal void NotifyRemoteWorkAvailable()
        => NotifyWorkAvailable(_remoteReadySignal, ref _remoteSignalPending);

    internal JobAuxiliaryWorkDomainMetrics GetMetrics()
        => new(
            WorkerCount,
            RunningWorkerCount,
            DeferredDispatchCount,
            DeferredWakeCount,
            RemoteDispatchCount,
            RemoteWakeCount);

    internal bool Shutdown(bool waitForWorkers)
        => Shutdown(waitForWorkers, WorkerJoinTimeout);

    internal bool Shutdown(bool waitForWorkers, TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) == 0)
        {
            ReleaseForShutdown(_deferredReadySignal);
            ReleaseForShutdown(_remoteReadySignal);
        }

        if (!waitForWorkers)
            return false;

        long deadline = CreateDeadline(timeout);
        bool deferredStopped = JoinWorker(_deferredEnqueueWorker, deadline);
        bool remoteStopped = JoinWorker(_remoteDispatchWorker, deadline);
        bool allStopped = deferredStopped && remoteStopped;
        if (allStopped && Interlocked.Exchange(ref _synchronizationDisposed, 1) == 0)
        {
            _deferredReadySignal.Dispose();
            _remoteReadySignal.Dispose();
        }

        return allStopped;
    }

    private void DeferredEnqueueWorkerLoop()
    {
        while (Volatile.Read(ref _shutdownState) == 0)
        {
            if (!WaitForWork(_deferredReadySignal, ref _deferredSignalPending))
                return;

            if (Volatile.Read(ref _shutdownState) != 0)
                return;

            Interlocked.Increment(ref _deferredWakeCount);
            while (Volatile.Read(ref _shutdownState) == 0 && _jobs.TryPromoteDeferredJob())
                Interlocked.Increment(ref _deferredDispatchCount);
        }
    }

    private void RemoteDispatchWorkerLoop()
    {
        JobManager.WorkerLaneState previousLane = JobManager.EnterGeneralWorkerLane(laneId: -1);
        try
        {
            while (Volatile.Read(ref _shutdownState) == 0)
            {
                if (!WaitForWork(_remoteReadySignal, ref _remoteSignalPending))
                    return;

                if (Volatile.Read(ref _shutdownState) != 0)
                    return;

                Interlocked.Increment(ref _remoteWakeCount);
                while (Volatile.Read(ref _shutdownState) == 0 && _jobs.TryDispatchRemoteJob())
                    Interlocked.Increment(ref _remoteDispatchCount);
            }
        }
        finally
        {
            JobManager.RestoreWorkerLane(previousLane);
        }
    }

    private void NotifyWorkAvailable(SemaphoreSlim signal, ref int signalPending)
    {
        if (Volatile.Read(ref _shutdownState) != 0 ||
            Interlocked.Exchange(ref signalPending, 1) != 0)
            return;

        try
        {
            signal.Release();
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref signalPending, 0);
        }
        catch (SemaphoreFullException)
        {
            // A coalesced notification is already available to the worker.
        }
    }

    private static bool WaitForWork(SemaphoreSlim signal, ref int signalPending)
    {
        try
        {
            signal.Wait();
            Volatile.Write(ref signalPending, 0);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void ReleaseForShutdown(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static bool JoinWorker(Thread worker, long deadline)
    {
        if (ReferenceEquals(worker, Thread.CurrentThread))
            return false;
        if (!worker.IsAlive)
            return true;

        TimeSpan remaining = GetRemaining(deadline);
        return remaining > TimeSpan.Zero && worker.Join(remaining);
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

    private static TimeSpan GetRemaining(long deadline)
    {
        long ticks = deadline - Stopwatch.GetTimestamp();
        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }
}
