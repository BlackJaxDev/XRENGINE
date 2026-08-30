using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Persistent scheduler-owned lanes that drain general <c>Any</c>-affinity work.
/// Foreground affinities remain phase-polled; auxiliary affinities have their
/// own scheduler domain.
/// </summary>
internal sealed class EngineGeneralWorkDomain
{
    private static readonly TimeSpan WorkerJoinTimeout = TimeSpan.FromSeconds(2);
    private readonly JobManager _jobs;
    private readonly Thread[] _workers;
    private readonly SemaphoreSlim _readySignal = new(0);
    private int _started;
    private int _shutdownState;
    private int _synchronizationDisposed;
    private long _dispatchCount;
    private long _wakeCount;
    private long _throttledDispatchCount;
    private long _throttleWaitTicks;

    [ThreadStatic]
    private static EngineGeneralWorkDomain?[]? _inlineDrainStack;

    [ThreadStatic]
    private static int _inlineDrainDepth;

    internal EngineGeneralWorkDomain(JobManager jobs, int workerCount)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (workerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _jobs = jobs;
        _workers = new Thread[workerCount];
        for (int laneId = 0; laneId < workerCount; laneId++)
        {
            _workers[laneId] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"XRE-General-{laneId}",
            };
        }
    }

    internal int WorkerCount => _workers.Length;
    internal long DispatchCount => Interlocked.Read(ref _dispatchCount);
    internal long WakeCount => Interlocked.Read(ref _wakeCount);
    internal long ThrottledDispatchCount
        => Interlocked.Read(ref _throttledDispatchCount);
    internal long ThrottleWaitTicks
        => Interlocked.Read(ref _throttleWaitTicks);

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The general work domain was already started.");

        try
        {
            for (int laneId = 0; laneId < _workers.Length; laneId++)
                _workers[laneId].Start(laneId);
        }
        catch (Exception exception)
        {
            if (!Shutdown(waitForWorkers: true))
            {
                Environment.FailFast(
                    "A general scheduler thread failed to start and the partially started domain did not quiesce.",
                    exception);
            }

            throw;
        }
    }

    internal void NotifyWorkAvailable()
    {
        if (Volatile.Read(ref _shutdownState) != 0)
            return;

        if (_workers.Length == 0)
        {
            DrainInline();
            return;
        }

        try
        {
            _readySignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    internal bool Shutdown(bool waitForWorkers)
        => Shutdown(waitForWorkers, WorkerJoinTimeout);

    internal bool Shutdown(bool waitForWorkers, TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) == 0)
        {
            if (_workers.Length == 0)
                return true;

            try
            {
                _readySignal.Release(_workers.Length);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        }

        if (!waitForWorkers)
            return false;

        long deadline = CreateDeadline(timeout);
        bool allStopped = true;
        foreach (Thread worker in _workers)
        {
            if (ReferenceEquals(worker, Thread.CurrentThread))
            {
                allStopped = false;
                continue;
            }

            if (!worker.IsAlive)
                continue;

            TimeSpan remaining = GetRemaining(deadline);
            if (remaining <= TimeSpan.Zero || !worker.Join(remaining))
                allStopped = false;
        }

        if (allStopped && Interlocked.Exchange(ref _synchronizationDisposed, 1) == 0)
            _readySignal.Dispose();

        return allStopped;
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

    private void DrainInline()
    {
        EngineGeneralWorkDomain?[] stack = _inlineDrainStack ??= new EngineGeneralWorkDomain[4];
        for (int index = 0; index < _inlineDrainDepth; index++)
            if (ReferenceEquals(stack[index], this))
                return;

        if (_inlineDrainDepth == stack.Length)
        {
            Array.Resize(ref stack, checked(stack.Length * 2));
            _inlineDrainStack = stack;
        }

        stack[_inlineDrainDepth++] = this;
        JobManager.WorkerLaneState previousLane = JobManager.EnterGeneralWorkerLane(laneId: 0);
        try
        {
            while (Volatile.Read(ref _shutdownState) == 0 &&
                   _jobs.TryDispatchGeneralWorkUnthrottled())
                Interlocked.Increment(ref _dispatchCount);
        }
        finally
        {
            JobManager.RestoreWorkerLane(previousLane);
            stack[--_inlineDrainDepth] = null;
        }
    }

    private void WorkerLoop(object? state)
    {
        int laneId = (int)state!;
        JobManager.WorkerLaneState previousLane = JobManager.EnterGeneralWorkerLane(laneId);
        try
        {
            while (Volatile.Read(ref _shutdownState) == 0)
            {
                try
                {
                    _readySignal.Wait();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (Volatile.Read(ref _shutdownState) != 0)
                    continue;

                Interlocked.Increment(ref _wakeCount);
                while (Volatile.Read(ref _shutdownState) == 0)
                {
                    if (_jobs.TryDispatchGeneralWork(out bool throttled))
                    {
                        Interlocked.Increment(ref _dispatchCount);
                        continue;
                    }
                    if (!throttled)
                        break;

                    long waitStarted = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _throttledDispatchCount);
                    JobManager.WaitForBackgroundDispatchPermission();
                    Interlocked.Add(
                        ref _throttleWaitTicks,
                        Math.Max(
                            1L,
                            Stopwatch.GetTimestamp() - waitStarted));
                }
            }
        }
        finally
        {
            JobManager.RestoreWorkerLane(previousLane);
        }
    }
}
