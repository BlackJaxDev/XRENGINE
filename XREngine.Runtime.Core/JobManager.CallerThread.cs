using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine;

public partial class JobManager
{
    private const int DefaultCallerThreadJobLimit = 64;
    private const double DefaultCallerThreadBudgetMilliseconds = 2.0;
    private readonly int _callerThreadId;
    private readonly Queue<Job> _callerPendingJobs = new();
    private readonly ConcurrentQueue<Job> _callerShutdownJobs = new();
    private int _callerQueueSlotsInUse;
    private bool _callerPumpActive;

    /// <summary>The execution strategy selected before any worker is created.</summary>
    public JobExecutionMode ExecutionMode { get; }

    public bool IsCallerThreadExecutor => ExecutionMode == JobExecutionMode.CallerThread;

    /// <summary>
    /// Advances one bounded slice of general or remote work on the creating
    /// thread. Foreground affinities retain their explicit frame-phase pumps.
    /// Async jobs are polled for readiness and never synchronously awaited.
    /// During shutdown this advances cancellation and returns no dispatches.
    /// </summary>
    /// <remarks>
    /// A job step and cancellation callback must themselves be cooperative;
    /// neither the count nor elapsed-time budget can preempt user code.
    /// A zero time budget uses only the supplied dispatch-count limit.
    /// </remarks>
    public int ProcessCallerThreadJobs(
        JobAffinity affinity = JobAffinity.Any,
        int maxJobs = DefaultCallerThreadJobLimit,
        double budgetMilliseconds = DefaultCallerThreadBudgetMilliseconds)
    {
        if (!IsCallerThreadExecutor)
            throw new InvalidOperationException("This JobManager uses worker-thread execution.");
        if (affinity is not (JobAffinity.Any or JobAffinity.Remote))
            throw new ArgumentOutOfRangeException(nameof(affinity), "Foreground affinities have frame-phase-specific pumps.");
        if (!double.IsFinite(budgetMilliseconds) || budgetMilliseconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));

        using CallerThreadPumpScope pump = EnterCallerThreadPump();
        if (!pump.CanProcess || maxJobs <= 0)
            return 0;

        long deadline = CreateCallerThreadDeadline(budgetMilliseconds);
        if (Volatile.Read(ref _shutdownState) != 0)
        {
            PumpCallerThreadShutdown(maxJobs, deadline);
            return 0;
        }

        PrepareCallerThreadWork(maxJobs, deadline);
        int remaining = Math.Min(maxJobs, SnapshotQueuedJobs(affinity));
        int processed = 0;
        while (processed < remaining && !CallerThreadBudgetExpired(deadline))
        {
            bool dispatched = affinity == JobAffinity.Remote
                ? TryDispatchRemoteJob()
                : TryDispatchGeneralWorkUnthrottled();
            if (!dispatched)
                break;
            processed++;
            if (Volatile.Read(ref _shutdownState) != 0)
                break;
        }

        return processed;
    }

    private CallerThreadPumpScope EnterCallerThreadPump()
    {
        if (!IsCallerThreadExecutor)
            return new CallerThreadPumpScope(null, canProcess: true);

        EnsureCallerThread();
        if (_callerPumpActive)
            return new CallerThreadPumpScope(null, canProcess: false);

        _callerPumpActive = true;
        return new CallerThreadPumpScope(this, canProcess: true);
    }

    private void EnsureCallerThread()
    {
        if (Environment.CurrentManagedThreadId != _callerThreadId)
            throw new InvalidOperationException("The caller-thread JobManager must be pumped and shut down on its creating thread.");
    }

    private bool PrepareCallerThreadPhase(int maxJobs)
    {
        if (!IsCallerThreadExecutor)
            return true;

        int limit = Math.Min(Math.Max(0, maxJobs), DefaultCallerThreadJobLimit);
        long deadline = CreateCallerThreadDeadline(DefaultCallerThreadBudgetMilliseconds);
        if (Volatile.Read(ref _shutdownState) != 0)
        {
            PumpCallerThreadShutdown(limit, deadline);
            return false;
        }

        PrepareCallerThreadWork(limit, deadline);
        return true;
    }

    private void PrepareCallerThreadWork(int maxJobs, long deadline)
    {
        PollCallerThreadPendingJobs(maxJobs, deadline);
        for (int i = 0; i < maxJobs && !CallerThreadBudgetExpired(deadline); i++)
        {
            if (!TryPromoteCallerThreadDeferredJob())
                break;
        }
    }

    private bool TryAcquireCallerThreadQueueSlot()
    {
        if (!IsQueueBounded)
            return true;

        lock (_submissionSync)
        {
            if (_callerQueueSlotsInUse >= _maxQueueSize)
                return false;
            Interlocked.Increment(ref _callerQueueSlotsInUse);
            return true;
        }
    }

    private bool TryPromoteCallerThreadDeferredJob()
    {
        EnsureCallerThread();
        if (Volatile.Read(ref _shutdownState) != 0 || _deferredBySlot.IsEmpty)
            return false;
        if (!TryAcquireCallerThreadQueueSlot())
            return false;

        if (!_deferredBySlot.TryDequeue(out Job? job))
        {
            if (IsQueueBounded)
                Interlocked.Decrement(ref _callerQueueSlotsInUse);
            return false;
        }

        job.UsesQueueSlot = IsQueueBounded;
        Enqueue(job, countAgainstSlots: false);
        return true;
    }

    private void PollCallerThreadPendingJobs(int maxJobs, long deadline)
    {
        int remaining = Math.Min(maxJobs, _callerPendingJobs.Count);
        for (int i = 0; i < remaining && !CallerThreadBudgetExpired(deadline); i++)
        {
            Job job = _callerPendingJobs.Dequeue();
            bool shuttingDown = Volatile.Read(ref _shutdownState) != 0;
            if (shuttingDown && !job.IsCompleted)
                job.RequestCancellationOnCallerThread();

            // Keep ownership until an async operation really ends, even when
            // cancellation has already been requested. It may still own assets.
            if (!job.IsCompleted && job.PendingTask is { IsCompleted: false })
            {
                _callerPendingJobs.Enqueue(job);
                continue;
            }

            if (shuttingDown && !job.IsCompleted)
            {
                if (job.PendingTask is { } completedTask)
                    ObserveShutdownOperation(completedTask);
                job.CompleteCancellationOnCallerThread();
            }

            if (!job.IsCompleted)
            {
                Requeue(job);
                continue;
            }

            if (job.TerminalNotificationTask.IsCompleted)
            {
                ObserveShutdownOperation(job.TerminalNotificationTask);
                RemoveActive(job);
            }
            else
                _callerPendingJobs.Enqueue(job);
        }
    }

    private void QueueCallerThreadShutdownJob(Job job)
    {
        if (job.TryClaimShutdownManagerFinalization())
            _callerShutdownJobs.Enqueue(job);
    }

    private bool ShutdownCallerThread()
    {
        EnsureCallerThread();
        lock (_submissionSync)
            Interlocked.Exchange(ref _shutdownState, 1);

        using CallerThreadPumpScope pump = EnterCallerThreadPump();
        if (!pump.CanProcess)
            return false;

        return PumpCallerThreadShutdown(
            DefaultCallerThreadJobLimit,
            CreateCallerThreadDeadline(DefaultCallerThreadBudgetMilliseconds));
    }

    private bool PumpCallerThreadShutdown(int maxJobs, long deadline)
    {
        PollCallerThreadPendingJobs(maxJobs, deadline);
        int processed = 0;
        while (processed < maxJobs && !CallerThreadBudgetExpired(deadline))
        {
            if (!_callerShutdownJobs.TryDequeue(out Job? job) &&
                !TryDequeueCallerThreadShutdownJob(out job))
                break;

            job.RequestCancellationOnCallerThread();
            if (job.PendingTask is not { IsCompleted: false })
            {
                if (job.PendingTask is { } completedTask)
                    ObserveShutdownOperation(completedTask);
                job.CompleteCancellationOnCallerThread();
            }

            if (job.IsCompleted && job.TerminalNotificationTask.IsCompleted)
            {
                ObserveShutdownOperation(job.TerminalNotificationTask);
                RemoveActive(job);
            }
            else
                _callerPendingJobs.Enqueue(job);
            processed++;
        }

        lock (_submissionSync)
        {
            if (_activeSubmissionCount != 0 || _callerPendingJobs.Count != 0 ||
                !_callerShutdownJobs.IsEmpty || !_deferredBySlot.IsEmpty ||
                SnapshotQueuedJobs(JobAffinity.Any) != 0 ||
                SnapshotQueuedJobs(JobAffinity.Remote) != 0 ||
                SnapshotQueuedJobs(JobAffinity.RenderThread) != 0 ||
                SnapshotQueuedJobs(JobAffinity.AppThread) != 0 ||
                SnapshotQueuedJobs(JobAffinity.CollectVisibleSwap) != 0)
                return false;

            lock (_activeLock)
                if (_active.Count != 0)
                    return false;

            if (Interlocked.Exchange(ref _shutdownSynchronizationDisposed, 1) == 0)
            {
                _activeJobsEmpty.Dispose();
                _submissionsEmpty.Dispose();
                _shutdownFinalizationsEmpty.Dispose();
                _cts.Dispose();
            }
            return true;
        }
    }

    private bool TryDequeueCallerThreadShutdownJob(out Job job)
    {
        if (TryDequeueWithAging(_pendingByPriority, JobAffinity.Any, out job, out _) ||
            TryDequeueWithAging(_pendingRemoteByPriority, JobAffinity.Remote, out job, out _) ||
            TryDequeueWithAging(_pendingMainThreadByPriority, JobAffinity.RenderThread, out job, out _) ||
            TryDequeueWithAging(_pendingAppThreadByPriority, JobAffinity.AppThread, out job, out _) ||
            TryDequeueWithAging(_pendingCollectVisibleSwapByPriority, JobAffinity.CollectVisibleSwap, out job, out _))
            return true;

        return _deferredBySlot.TryDequeue(out job!);
    }

    private int SnapshotQueuedJobs(JobAffinity affinity)
    {
        int count = 0;
        for (int p = 0; p < PriorityLevels; p++)
            count += Math.Max(0, GetQueuedCount((JobPriority)p, affinity));
        return count;
    }

    private static long CreateCallerThreadDeadline(double budgetMilliseconds)
        => budgetMilliseconds <= 0.0
            ? long.MaxValue
            : CreateShutdownDeadline(TimeSpan.FromMilliseconds(budgetMilliseconds));

    private static bool CallerThreadBudgetExpired(long deadline)
        => Stopwatch.GetTimestamp() >= deadline;

    private readonly struct CallerThreadPumpScope(JobManager? owner, bool canProcess) : IDisposable
    {
        public bool CanProcess { get; } = canProcess;

        public void Dispose()
        {
            if (owner is not null)
                owner._callerPumpActive = false;
        }
    }
}
