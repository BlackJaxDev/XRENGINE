using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine
{
    public class JobManager
    {
        [ThreadStatic]
        private static bool _isJobWorkerThread;

        [ThreadStatic]
        private static Job? _currentExecutingJob;

        [ThreadStatic]
        private static int _currentGeneralWorkerLaneId;

        /// <summary>
        /// True when executing on any JobManager worker thread (including remote dispatch worker).
        /// </summary>
        public static bool IsJobWorkerThread => _isJobWorkerThread;

        /// <summary>
        /// Stable scheduler lane for the current general worker, or <c>-1</c>
        /// when the current thread is not executing in the general domain.
        /// </summary>
        public static int CurrentGeneralWorkerLaneId
            => _isJobWorkerThread ? _currentGeneralWorkerLaneId : -1;

        public static Action<string>? LogMessage { get; set; }
        public static Func<string, IDisposable?>? ProfilerScopeFactory { get; set; }
        public static Action<JobAffinity, string, RenderThreadJobKind>? JobDispatchObserver { get; set; }
        public static Action<string, RenderThreadJobKind, double, double, double>? RenderThreadJobExecutionObserver { get; set; }

        private const int PriorityLevels = 5; // Matches JobPriority enum
        private const int DefaultWorkerCap = 16;
        private const int DefaultReservedThreads = 4; // render + update + fixed update + collect visible / swap buffers
        private const int DefaultQueueWarningThreshold = 2048;
        private const int DefaultQueueLimit = 8192;
        private const int QueueAcquireWaitMs = 50;
        private static readonly TimeSpan StarvationWarningThreshold = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan StarvationLogInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BackpressureLogInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan SlowRenderThreadJobLogInterval = TimeSpan.FromSeconds(1);
        private static readonly long StarvationWarningTicks = (long)(StarvationWarningThreshold.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        private static readonly long SlowRenderThreadJobTicks = (long)(System.Diagnostics.Stopwatch.Frequency / 1000.0);
        // Render-thread jobs that exceed this duration always log (bypass per-label dedup) and emit a memory snapshot.
        // Tuned to catch the ~hundreds-of-ms freezes that precede OS-level memory-pressure kills without spamming on normal slow frames.
        private static readonly long StallRenderThreadJobTicks = (long)(System.Diagnostics.Stopwatch.Frequency * 0.250);
        private static readonly TimeSpan RemoteWorkerIdleTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ShutdownTaskWaitTimeout = TimeSpan.FromSeconds(2);
        private readonly ConcurrentQueue<Job>[] _pendingByPriority =
        [
            new(),
            new(),
            new(),
            new(),
            new(),
        ];
        private readonly ConcurrentQueue<Job>[] _pendingMainThreadByPriority =
        [
            new(),
            new(),
            new(),
            new(),
            new(),
        ];
        private readonly ConcurrentQueue<Job>[] _pendingAppThreadByPriority =
        [
            new(),
            new(),
            new(),
            new(),
            new(),
        ];
        private readonly ConcurrentQueue<Job>[] _pendingCollectVisibleSwapByPriority =
        [
            new(),
            new(),
            new(),
            new(),
            new(),
        ];
        private readonly ConcurrentQueue<Job>[] _pendingRemoteByPriority =
        [
            new(),
            new(),
            new(),
            new(),
            new(),
        ];
        private readonly List<Job> _active = new();
        private readonly object _activeLock = new();
        private readonly ConcurrentDictionary<string, long> _lastStarvationLogTicksByLabel = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _lastSlowRenderThreadLogTicksByLabel = new(StringComparer.Ordinal);

        private readonly int[] _pendingCounts = new int[PriorityLevels];
        private readonly int[] _pendingMainThreadCounts = new int[PriorityLevels];
        private readonly int[] _pendingAppThreadCounts = new int[PriorityLevels];
        private readonly int[] _pendingCollectCounts = new int[PriorityLevels];
        private readonly int[] _pendingRemoteCounts = new int[PriorityLevels];
        private readonly long[] _totalWaitTicks = new long[PriorityLevels];
        private readonly long[] _waitSamples = new long[PriorityLevels];
        private readonly long[] _lastQueueWarningTicks = new long[PriorityLevels];
        private readonly SemaphoreSlim? _queueSlots;
        private readonly int _queueWarningThreshold;
        private readonly int _maxQueueSize;

        private readonly SemaphoreSlim _remoteReadySignal = new(0);
        private readonly SemaphoreSlim _deferredReadySignal = new(0);
        private readonly ConcurrentQueue<Job> _deferredBySlot = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly int _configuredWorkerCount;
        private Execution.EngineGeneralWorkDomain? _generalDomain;
        private readonly object _remoteWorkerLock = new();
        private Task? _remoteWorkerTask;

        private readonly object _deferredWorkerLock = new();
        private Task? _deferredWorkerTask;
        private readonly ManualResetEventSlim _activeJobsEmpty = new(initialState: true);
        private readonly ManualResetEventSlim _submissionsEmpty = new(initialState: true);
        private readonly Queue<Job> _shutdownFinalizationQueue = new();
        private readonly ManualResetEventSlim _shutdownFinalizationsEmpty = new(initialState: true);
        private readonly object _shutdownFinalizationLock = new();
        private Task? _shutdownFinalizerTask;
        private int _shutdownFinalizationCount;
        private int _shutdownFinalizationAdmissionClosed;
        private int _activeSubmissionCount;
        private readonly object _submissionSync = new();
        private readonly object _shutdownJoinLock = new();
        private int _shutdownState;
        private int _shutdownSynchronizationDisposed;

        public int WorkerCount => _generalDomain?.WorkerCount ?? _configuredWorkerCount;
        public IRemoteJobTransport? RemoteTransport { get; set; }

        public JobManager(int? workerCount = null, int? maxQueueSize = null, int? queueWarningThreshold = null, int? workerCap = null)
            : this(workerCount, maxQueueSize, queueWarningThreshold, workerCap, createGeneralWorkers: true)
        {
        }

        internal JobManager(
            int? workerCount,
            int? maxQueueSize,
            int? queueWarningThreshold,
            int? workerCap,
            bool createGeneralWorkers)
        {
            int cap = workerCap ?? ReadWorkerCapFromEnv() ?? DefaultWorkerCap;
            int reserved = Math.Max(0, DefaultReservedThreads);
            int defaultWorkers = Math.Max(1, Environment.ProcessorCount - reserved);
            defaultWorkers = Math.Min(defaultWorkers, cap);

            int count = workerCount ?? ReadWorkerCountFromEnv() ?? defaultWorkers;
            count = Math.Clamp(count, createGeneralWorkers ? 1 : 0, cap);
            _configuredWorkerCount = count;

            _maxQueueSize = maxQueueSize ?? ReadQueueLimitFromEnv() ?? DefaultQueueLimit;
            _queueWarningThreshold = queueWarningThreshold ?? ReadQueueWarningThresholdFromEnv() ?? DefaultQueueWarningThreshold;
            _queueWarningThreshold = Math.Max(_queueWarningThreshold, PriorityLevels);

            if (_maxQueueSize > 0 && _queueWarningThreshold > _maxQueueSize)
                _queueWarningThreshold = _maxQueueSize;

            if (_maxQueueSize > 0)
                _queueSlots = new SemaphoreSlim(_maxQueueSize, _maxQueueSize);

            if (createGeneralWorkers)
                AttachGeneralDomain(new Execution.EngineGeneralWorkDomain(this, count));
        }

        internal void AttachGeneralDomain(Execution.EngineGeneralWorkDomain domain)
        {
            ArgumentNullException.ThrowIfNull(domain);
            if (Interlocked.CompareExchange(ref _generalDomain, domain, null) is not null)
                throw new InvalidOperationException("The JobManager general execution domain is already installed.");

            try
            {
                domain.Start();
            }
            catch
            {
                Shutdown(waitForWorkers: true);
                throw;
            }
        }

        public IReadOnlyCollection<Job> Active
        {
            get
            {
                lock (_activeLock)
                    return [.. _active];
            }
        }

        public int GetQueuedCount(JobPriority priority)
            => GetQueuedCount(priority, JobAffinity.Any);

        public int GetQueuedCount(JobPriority priority, JobAffinity affinity)
        {
            int bucket = Math.Clamp((int)priority, 0, PriorityLevels - 1);
            return affinity switch
            {
                JobAffinity.RenderThread => Volatile.Read(ref _pendingMainThreadCounts[bucket]),
                JobAffinity.AppThread => Volatile.Read(ref _pendingAppThreadCounts[bucket]),
                JobAffinity.CollectVisibleSwap => Volatile.Read(ref _pendingCollectCounts[bucket]),
                JobAffinity.Remote => Volatile.Read(ref _pendingRemoteCounts[bucket]),
                _ => Volatile.Read(ref _pendingCounts[bucket]),
            };
        }

        public bool IsQueueBounded => _queueSlots != null;

        public int QueueCapacity => _queueSlots != null ? _maxQueueSize : int.MaxValue;

        public int QueueSlotsAvailable => _queueSlots?.CurrentCount ?? int.MaxValue;

        public int QueueSlotsInUse
        {
            get
            {
                if (_queueSlots is null)
                    return 0;

                int available = _queueSlots.CurrentCount;
                return Math.Max(0, _maxQueueSize - available);
            }
        }

        public TimeSpan GetAverageWait(JobPriority priority)
        {
            int bucket = Math.Clamp((int)priority, 0, PriorityLevels - 1);
            long samples = Volatile.Read(ref _waitSamples[bucket]);
            if (samples == 0)
                return TimeSpan.Zero;

            long total = Volatile.Read(ref _totalWaitTicks[bucket]);
            double averageTicks = total / (double)samples;
            return TimeSpan.FromSeconds(averageTicks / System.Diagnostics.Stopwatch.Frequency);
        }

        public JobHandle Schedule(Job job)
            => Schedule(job, JobPriority.Normal, JobAffinity.Any, CancellationToken.None);

        public JobHandle Schedule(Job job, CancellationToken cancellationToken)
            => Schedule(job, JobPriority.Normal, JobAffinity.Any, cancellationToken);

        public JobHandle Schedule(
            Job job,
            JobPriority priority,
            JobAffinity affinity = JobAffinity.Any,
            CancellationToken cancellationToken = default,
            RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (!TryEnterSubmission())
                return RejectAfterShutdown(job, priority, affinity, renderThreadKind);

            try
            {
                if (!job.TryStart())
                    throw new InvalidOperationException("Job has already been scheduled or completed.");

                ConfigureStartedJob(job, priority, affinity, renderThreadKind, cancellationToken);

                if (Volatile.Read(ref _shutdownState) != 0)
                {
                    CancelQueuedJobForShutdown(job);
                    return job.Handle;
                }

                if (_queueSlots is null)
                {
                    Enqueue(job, countAgainstSlots: false);
                    return job.Handle;
                }

                bool acquired = false;
                try
                {
                    acquired = _queueSlots.Wait(0);
                }
                catch (ObjectDisposedException)
                {
                    acquired = false;
                }

                if (acquired)
                {
                    job.UsesQueueSlot = true;
                    Enqueue(job, countAgainstSlots: false);
                    return job.Handle;
                }

                DeferEnqueue(job);
                return job.Handle;
            }
            finally
            {
                ExitSubmission();
            }
        }

        private bool TryEnterSubmission()
        {
            lock (_submissionSync)
            {
                if (Volatile.Read(ref _shutdownState) != 0)
                    return false;

                if (_activeSubmissionCount++ == 0)
                    _submissionsEmpty.Reset();
                return true;
            }
        }

        private void ExitSubmission()
        {
            lock (_submissionSync)
            {
                if (--_activeSubmissionCount == 0)
                    _submissionsEmpty.Set();
            }
        }

        private JobHandle RejectAfterShutdown(
            Job job,
            JobPriority priority,
            JobAffinity affinity,
            RenderThreadJobKind renderThreadKind)
        {
            if (!job.TryStartForShutdownRejection())
                throw new InvalidOperationException("Job has already been scheduled or completed.");

            ConfigureStartedJob(job, priority, affinity, renderThreadKind, CancellationToken.None);
            CancelQueuedJobForShutdown(job);
            return job.Handle;
        }

        private static void ConfigureStartedJob(
            Job job,
            JobPriority priority,
            JobAffinity affinity,
            RenderThreadJobKind renderThreadKind,
            CancellationToken cancellationToken)
        {
            job.Priority = priority;
            job.Affinity = affinity;
            job.RenderThreadKind = affinity == JobAffinity.RenderThread
                ? renderThreadKind
                : RenderThreadJobKind.Unknown;
            job.LinkCancellationToken(cancellationToken);
            job.AttachCompletionSource(
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private void DeferEnqueue(Job job)
        {
            bool rejected = false;
            lock (_submissionSync)
            {
                if (Volatile.Read(ref _shutdownState) != 0)
                    rejected = true;
                else
                {
                    _deferredBySlot.Enqueue(job);
                    EnsureDeferredWorker();
                }
            }

            if (rejected)
            {
                CancelQueuedJobForShutdown(job);
                return;
            }

            try
            {
                _deferredReadySignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void EnsureDeferredWorker()
        {
            lock (_deferredWorkerLock)
            {
                if (_deferredWorkerTask is { IsCompleted: false })
                    return;

                _deferredWorkerTask = Task.Factory.StartNew(
                    DeferredEnqueueLoop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        private void DeferredEnqueueLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    _deferredReadySignal.Wait(_cts.Token);

                    while (_deferredBySlot.TryDequeue(out var job))
                    {
                        if (_cts.IsCancellationRequested)
                        {
                            CancelQueuedJobForShutdown(job);
                            return;
                        }

                        bool acquired = AcquireQueueSlot(_cts.Token);
                        if (acquired)
                            job.UsesQueueSlot = _queueSlots is not null;

                        if (!acquired || _cts.IsCancellationRequested)
                        {
                            CancelQueuedJobForShutdown(job);
                            return;
                        }

                        Enqueue(job, countAgainstSlots: false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public EnumeratorJob Schedule(
            IEnumerable routine,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default,
            JobPriority priority = JobPriority.Normal)
        {
            var job = new EnumeratorJob(routine, progress, completed, error, canceled, progressWithPayload);
            _ = Schedule(job, priority, JobAffinity.Any, cancellationToken);
            return job;
        }

        public EnumeratorJob Schedule(
            Func<IEnumerable> routineFactory,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default,
            JobPriority priority = JobPriority.Normal)
        {
            var job = new EnumeratorJob(routineFactory, progress, completed, error, canceled, progressWithPayload);
            _ = Schedule(job, priority, JobAffinity.Any, cancellationToken);
            return job;
        }

        public Task<RemoteJobResponse> ScheduleRemote(RemoteJobRequest request, JobPriority priority = JobPriority.Normal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var transport = RemoteTransport ?? throw new InvalidOperationException("Remote transport has not been configured.");
            var tcs = new TaskCompletionSource<RemoteJobResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new RemoteDispatchJob(request, transport, tcs);

            Schedule(job, priority, JobAffinity.Remote, cancellationToken);
            return tcs.Task;
        }

        public static bool Cancel(Job job)
        {
            ArgumentNullException.ThrowIfNull(job);
            job.Cancel();
            return true;
        }

        public bool Cancel(Guid jobId)
        {
            Job? target = null;

            lock (_activeLock)
            {
                foreach (var job in _active)
                {
                    if (job.Id == jobId)
                    {
                        target = job;
                        break;
                    }
                }
            }

            if (target != null)
            {
                target.Cancel();
                return true;
            }

            foreach (var queue in _pendingByPriority)
                foreach (var pending in queue)
                    if (pending.Id == jobId)
                    {
                        pending.Cancel();
                        return true;
                    }

            foreach (var queue in _pendingMainThreadByPriority)
                foreach (var pending in queue)
                    if (pending.Id == jobId)
                    {
                        pending.Cancel();
                        return true;
                    }

            foreach (var queue in _pendingAppThreadByPriority)
                foreach (var pending in queue)
                    if (pending.Id == jobId)
                    {
                        pending.Cancel();
                        return true;
                    }

            foreach (var queue in _pendingCollectVisibleSwapByPriority)
                foreach (var pending in queue)
                    if (pending.Id == jobId)
                    {
                        pending.Cancel();
                        return true;
                    }

            return false;
        }

        private void Enqueue(Job job, bool countAgainstSlots)
        {
            if (countAgainstSlots)
            {
                bool acquired = AcquireQueueSlot(job.CancellationToken);
                if (!acquired)
                {
                    CancelQueuedJobForShutdown(job);
                    return;
                }

                job.UsesQueueSlot = _queueSlots is not null;
            }

            bool notifyGeneral = false;
            bool notifyRemote = false;
            bool rejected = false;
            lock (_submissionSync)
            {
                if (Volatile.Read(ref _shutdownState) != 0)
                    rejected = true;
                else
                {
                    int bucket = Math.Clamp((int)job.Priority, 0, PriorityLevels - 1);
                    job.MarkQueued(System.Diagnostics.Stopwatch.GetTimestamp());
                    IncrementCounts(job.Affinity, bucket);

                    switch (job.Affinity)
                    {
                        case JobAffinity.RenderThread:
                            _pendingMainThreadByPriority[bucket].Enqueue(job);
                            break;
                        case JobAffinity.AppThread:
                            _pendingAppThreadByPriority[bucket].Enqueue(job);
                            break;
                        case JobAffinity.CollectVisibleSwap:
                            _pendingCollectVisibleSwapByPriority[bucket].Enqueue(job);
                            break;
                        case JobAffinity.Remote:
                            EnsureRemoteWorker();
                            _pendingRemoteByPriority[bucket].Enqueue(job);
                            notifyRemote = true;
                            break;
                        default:
                            _pendingByPriority[bucket].Enqueue(job);
                            notifyGeneral = true;
                            break;
                    }
                }
            }

            if (rejected)
            {
                CancelQueuedJobForShutdown(job);
                return;
            }

            if (notifyGeneral)
                _generalDomain?.NotifyWorkAvailable();
            if (!notifyRemote)
                return;

            try
            {
                _remoteReadySignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool AcquireQueueSlot(CancellationToken cancellationToken)
        {
            if (_queueSlots is null)
                return true;

            long lastLogTick = System.Diagnostics.Stopwatch.GetTimestamp();

            while (true)
            {
                if (_cts.IsCancellationRequested)
                    return false;

                if (cancellationToken.IsCancellationRequested)
                    return false;

                try
                {
                    if (_queueSlots.Wait(QueueAcquireWaitMs, cancellationToken))
                        return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (TicksToTimeSpan(now - lastLogTick) >= BackpressureLogInterval)
                {
                    Log($"Job queue back-pressure: waiting for free slot (limit {_maxQueueSize}).");
                    lastLogTick = now;
                }
            }
        }

        private void IncrementCounts(JobAffinity affinity, int bucket)
        {
            int newCount = affinity switch
            {
                JobAffinity.RenderThread => Interlocked.Increment(ref _pendingMainThreadCounts[bucket]),
                JobAffinity.AppThread => Interlocked.Increment(ref _pendingAppThreadCounts[bucket]),
                JobAffinity.CollectVisibleSwap => Interlocked.Increment(ref _pendingCollectCounts[bucket]),
                JobAffinity.Remote => Interlocked.Increment(ref _pendingRemoteCounts[bucket]),
                _ => Interlocked.Increment(ref _pendingCounts[bucket]),
            };

            if (affinity == JobAffinity.Any)
                MaybeLogQueueLength(bucket, newCount);
        }

        private void DecrementCounts(JobAffinity affinity, int bucket)
        {
            switch (affinity)
            {
                case JobAffinity.RenderThread:
                    Interlocked.Decrement(ref _pendingMainThreadCounts[bucket]);
                    break;
                case JobAffinity.AppThread:
                    Interlocked.Decrement(ref _pendingAppThreadCounts[bucket]);
                    break;
                case JobAffinity.CollectVisibleSwap:
                    Interlocked.Decrement(ref _pendingCollectCounts[bucket]);
                    break;
                case JobAffinity.Remote:
                    Interlocked.Decrement(ref _pendingRemoteCounts[bucket]);
                    break;
                default:
                    Interlocked.Decrement(ref _pendingCounts[bucket]);
                    break;
            }
        }

        private void MaybeLogQueueLength(int bucket, int queuedCount)
        {
            if (_queueWarningThreshold <= 0 || queuedCount < _queueWarningThreshold)
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long lastLog = Volatile.Read(ref _lastQueueWarningTicks[bucket]);
            if (TicksToTimeSpan(now - lastLog) < BackpressureLogInterval)
                return;

            if (Interlocked.CompareExchange(ref _lastQueueWarningTicks[bucket], now, lastLog) == lastLog)
                Log($"Job queue [{(JobPriority)bucket}] length {queuedCount} exceeds threshold {_queueWarningThreshold} (cap {_maxQueueSize}).");
        }

        private void RecordWait(Job job, int bucket)
        {
            long lastQueued = job.LastEnqueuedTimestamp;
            if (lastQueued == 0)
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long waitedTicks = now - lastQueued;

            Interlocked.Add(ref _totalWaitTicks[bucket], waitedTicks);
            Interlocked.Increment(ref _waitSamples[bucket]);

            double waitMs = waitedTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            if (waitMs >= StarvationWarningThreshold.TotalMilliseconds && job.TryMarkStarvationLogged())
            {
                string label = job.GetProfilerLabel();
                if (ShouldLogStarvation(label, job.Affinity, job.Priority, now))
                    Log($"Job {job.Id} [{job.Affinity}/{job.Priority}] {label} waited {waitMs:F1} ms before execution.");
            }
        }

        private bool ShouldLogStarvation(string label, JobAffinity affinity, JobPriority priority, long now)
        {
            string key = $"{affinity}|{priority}|{label}";

            while (true)
            {
                if (_lastStarvationLogTicksByLabel.TryGetValue(key, out long lastLogTick))
                {
                    if (TicksToTimeSpan(now - lastLogTick) < StarvationLogInterval)
                        return false;

                    if (_lastStarvationLogTicksByLabel.TryUpdate(key, now, lastLogTick))
                        return true;

                    continue;
                }

                if (_lastStarvationLogTicksByLabel.TryAdd(key, now))
                    return true;
            }
        }

        private bool ShouldLogSlowRenderThreadJob(string label, JobPriority priority, long now)
        {
            string key = $"{priority}|{label}";

            while (true)
            {
                if (_lastSlowRenderThreadLogTicksByLabel.TryGetValue(key, out long lastLogTick))
                {
                    if (TicksToTimeSpan(now - lastLogTick) < SlowRenderThreadJobLogInterval)
                        return false;

                    if (_lastSlowRenderThreadLogTicksByLabel.TryUpdate(key, now, lastLogTick))
                        return true;

                    continue;
                }

                if (_lastSlowRenderThreadLogTicksByLabel.TryAdd(key, now))
                    return true;
            }
        }

        private void MaybeLogSlowRenderThreadJob(Job job, string label, long elapsedTicks, double budgetMilliseconds)
        {
            if (elapsedTicks < SlowRenderThreadJobTicks)
                return;

            bool isStall = elapsedTicks >= StallRenderThreadJobTicks;
            long now = Stopwatch.GetTimestamp();
            // Stall-level jobs always log (bypass dedup) so we never miss the actual freeze culprit.
            if (!isStall && !ShouldLogSlowRenderThreadJob(label, job.Priority, now))
                return;

            double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            int queuedRenderJobs = SnapshotQueuedMainThreadJobs();
            string budgetText = budgetMilliseconds > 0.0
                ? $", budget={budgetMilliseconds:F1} ms"
                : string.Empty;
            string memoryText = string.Empty;
            if (isStall)
            {
                try
                {
                    long managedMB = GC.GetTotalMemory(forceFullCollection: false) >> 20;
                    long workingSetMB;
                    using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess())
                        workingSetMB = proc.WorkingSet64 >> 20;
                    memoryText = $", managedHeapMB={managedMB}, workingSetMB={workingSetMB}";
                }
                catch
                {
                    // Process memory snapshot is best-effort; never let a memory probe failure block diagnostics output.
                }
            }
            Log(
                $"[RenderThreadJobs] {(isStall ? "STALL" : "Slow")} render-thread job '{label}' [{job.Priority}] took {elapsedMilliseconds:F2} ms in ProcessMainThreadJobs "
                + $"(queuedRender={queuedRenderJobs}{budgetText}{memoryText}).");
        }

        private static TimeSpan TicksToTimeSpan(long ticks)
            => TimeSpan.FromSeconds(ticks / (double)System.Diagnostics.Stopwatch.Frequency);

        private static void Log(string message)
            => LogMessage?.Invoke(message);

        private static IDisposable? StartProfilerScope(string name)
            => ProfilerScopeFactory?.Invoke(name);

        public bool Process()
        {
            return TryDispatchGeneralWork();
        }

        private void RemoteWorkerLoop()
        {
            WorkerLaneState previousLane = EnterGeneralWorkerLane(laneId: -1);
            try
            {
                var token = _cts.Token;
                while (!token.IsCancellationRequested)
                {
                    bool signaled;
                    try
                    {
                        signaled = _remoteReadySignal.Wait(RemoteWorkerIdleTimeout, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!signaled)
                    {
                        if (IsRemoteQueueEmpty())
                            return;

                        continue;
                    }

                    if (!TryDequeueWithAging(_pendingRemoteByPriority, JobAffinity.Remote, out var job, out var bucket))
                        continue;

                    RecordWait(job, bucket);
                    ExecuteJob(job);
                }
            }
            finally
            {
                ClearRemoteWorkerTask();
                RestoreWorkerLane(previousLane);
            }
        }

        private bool IsRemoteQueueEmpty()
        {
            for (int i = 0; i < PriorityLevels; i++)
                if (!_pendingRemoteByPriority[i].IsEmpty)
                    return false;

            return true;
        }

        private void ClearRemoteWorkerTask()
        {
            lock (_remoteWorkerLock)
            {
                if (Volatile.Read(ref _shutdownState) == 0)
                    _remoteWorkerTask = null;
            }
        }

        private void EnsureRemoteWorker()
        {
            if (_remoteWorkerTask is { IsCompleted: false })
                return;

            lock (_remoteWorkerLock)
            {
                if (_remoteWorkerTask is { IsCompleted: false })
                    return;

                _remoteWorkerTask = Task.Run(RemoteWorkerLoop, _cts.Token);
            }
        }

        private bool TryDequeueWithAging(ConcurrentQueue<Job>[] queues, JobAffinity affinity, out Job job, out int bucket)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long bestWait = -1;
            int bestBucket = -1;

            for (int p = 0; p < PriorityLevels; p++)
            {
                if (!queues[p].TryPeek(out var peeked))
                    continue;

                long lastQueued = peeked.LastEnqueuedTimestamp;
                if (lastQueued == 0)
                    continue;

                long waited = now - lastQueued;
                if (waited >= StarvationWarningTicks && waited > bestWait)
                {
                    bestWait = waited;
                    bestBucket = p;
                }
            }

            if (bestBucket >= 0 && queues[bestBucket].TryDequeue(out job!))
            {
                bucket = bestBucket;
                DecrementCounts(affinity, bucket);
                return true;
            }

            for (int p = PriorityLevels - 1; p >= 0; p--)
            {
                if (queues[p].TryDequeue(out job!))
                {
                    bucket = p;
                    DecrementCounts(affinity, p);
                    return true;
                }
            }

            job = null!;
            bucket = -1;
            return false;
        }

        private void ExecuteJob(Job job)
        {
            bool admitted = false;
            lock (_submissionSync)
            {
                if (Volatile.Read(ref _shutdownState) == 0)
                {
                    lock (_activeLock)
                    {
                        if (!_active.Contains(job))
                        {
                            if (_active.Count == 0)
                                _activeJobsEmpty.Reset();
                            _active.Add(job);
                        }
                    }

                    admitted = true;
                }
            }

            if (!admitted)
            {
                CancelQueuedJobForShutdown(job);
                return;
            }

            const int MaxStepsPerDispatch = 64;
            int steps = 0;

            Job? previousJob = _currentExecutingJob;
            _currentExecutingJob = job;
            try
            {
                while (true)
                {
                    JobStepResult result;
                    try
                    {
                        result = job.Step();
                    }
                    catch (Exception ex)
                    {
                        job.Fail(ex);
                        ReleaseAfterTerminalNotification(job);
                        return;
                    }

                    switch (result)
                    {
                        case JobStepResult.Completed:
                            ReleaseAfterTerminalNotification(job);
                            return;
                        case JobStepResult.Waiting:
                            if (job.PendingTask is { IsCompleted: false } pending)
                            {
                                pending.ContinueWith(_ => Requeue(job), TaskContinuationOptions.ExecuteSynchronously);
                                return;
                            }
                            Requeue(job);
                            return;
                        case JobStepResult.Progressed:
                            steps++;
                            if (steps >= MaxStepsPerDispatch)
                            {
                                Requeue(job);
                                return;
                            }
                            continue;
                        case JobStepResult.Idle:
                        default:
                            Requeue(job);
                            return;
                    }
                }
            }
            finally
            {
                _currentExecutingJob = previousJob;
            }
        }

        private void RemoveActive(Job job)
        {
            bool removed;
            lock (_activeLock)
            {
                removed = _active.Remove(job);
                if (removed && _active.Count == 0 &&
                    Volatile.Read(ref _shutdownSynchronizationDisposed) == 0)
                {
                    try
                    {
                        _activeJobsEmpty.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            ReleaseQueueSlot(job);
        }

        private void ReleaseAfterTerminalNotification(Job job)
        {
            Task notification = job.TerminalNotificationTask;
            if (notification.IsCompleted)
            {
                ObserveShutdownOperation(notification);
                RemoveActive(job);
                return;
            }

            _ = notification.ContinueWith(
                completed =>
                {
                    ObserveShutdownOperation(completed);
                    RemoveActive(job);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void Requeue(Job job)
        {
            if (Volatile.Read(ref _shutdownState) != 0)
            {
                CancelActiveJobForShutdown(job);
                return;
            }

            Enqueue(job, countAgainstSlots: false);
        }

        internal bool TryDispatchGeneralWork()
        {
            if (!TryDequeueWithAging(_pendingByPriority, JobAffinity.Any, out var job, out var bucket))
                return false;

            RecordWait(job, bucket);
            ExecuteJob(job);
            return true;
        }

        internal readonly record struct WorkerLaneState(bool IsWorkerThread, int LaneId);

        internal static WorkerLaneState EnterGeneralWorkerLane(int laneId)
        {
            var previous = new WorkerLaneState(_isJobWorkerThread, _currentGeneralWorkerLaneId);
            _currentGeneralWorkerLaneId = laneId;
            _isJobWorkerThread = true;
            return previous;
        }

        internal static void RestoreWorkerLane(in WorkerLaneState previous)
        {
            _isJobWorkerThread = previous.IsWorkerThread;
            _currentGeneralWorkerLaneId = previous.LaneId;
        }

        private int SnapshotQueuedMainThreadJobs()
        {
            int total = 0;
            for (int i = 0; i < PriorityLevels; i++)
                total += Math.Max(0, Volatile.Read(ref _pendingMainThreadCounts[i]));
            return total;
        }

        private int SnapshotQueuedAppThreadJobs()
        {
            int total = 0;
            for (int i = 0; i < PriorityLevels; i++)
                total += Math.Max(0, Volatile.Read(ref _pendingAppThreadCounts[i]));
            return total;
        }

        /// <summary>
        /// Drains main-thread jobs that were already queued when this method begins.
        /// This method never waits/spins for more work, and it will not chase newly-enqueued jobs.
        /// Stops early when the time budget is exceeded, deferring remaining jobs to the next call.
        /// </summary>
        /// <param name="maxJobs">Maximum number of jobs to process per call.</param>
        /// <param name="budgetMilliseconds">
        /// Time budget in milliseconds. After each job completes, if cumulative elapsed time exceeds
        /// this budget the method returns, leaving remaining jobs for the next frame. A value of
        /// <c>0</c> disables the time budget (count-only limiting).
        /// </param>
        public void ProcessMainThreadJobs(int maxJobs = int.MaxValue, double budgetMilliseconds = 0.0)
        {
            int snapshot = SnapshotQueuedMainThreadJobs();
            int remaining = Math.Min(Math.Max(0, maxJobs), snapshot);

            long budgetTicks = budgetMilliseconds > 0.0
                ? (long)(budgetMilliseconds * Stopwatch.Frequency / 1000.0)
                : 0L;
            long start = budgetTicks > 0L ? Stopwatch.GetTimestamp() : 0L;

            int processed = 0;
            while (processed < remaining && TryDequeueWithAging(_pendingMainThreadByPriority, JobAffinity.RenderThread, out var job, out var bucket))
            {
                long dequeuedAt = Stopwatch.GetTimestamp();
                long queuedAt = job.LastEnqueuedTimestamp;
                double queueDelayMilliseconds = queuedAt == 0L
                    ? 0.0
                    : Math.Max(0L, dequeuedAt - queuedAt) * 1000.0 / Stopwatch.Frequency;
                RecordWait(job, bucket);
                string label = job.GetProfilerLabel();
                JobDispatchObserver?.Invoke(JobAffinity.RenderThread, label, job.RenderThreadKind);
                long jobStart = Stopwatch.GetTimestamp();
                using (StartProfilerScope($"MainThreadJobs.{job.Priority}.{label}"))
                {
                    ExecuteJob(job);
                }
                long jobElapsedTicks = Stopwatch.GetTimestamp() - jobStart;
                double durationMilliseconds = jobElapsedTicks * 1000.0 / Stopwatch.Frequency;
                double overBudgetMilliseconds = budgetMilliseconds <= 0.0
                    ? 0.0
                    : Math.Max(0.0, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency - budgetMilliseconds);
                RenderThreadJobExecutionObserver?.Invoke(
                    label,
                    job.RenderThreadKind,
                    durationMilliseconds,
                    queueDelayMilliseconds,
                    overBudgetMilliseconds);
                MaybeLogSlowRenderThreadJob(job, label, jobElapsedTicks, budgetMilliseconds);
                processed++;

                // Check time budget after each job completes.
                if (budgetTicks > 0L && Stopwatch.GetTimestamp() - start > budgetTicks)
                    break;
            }
        }

        public void ProcessAppThreadJobs(int maxJobs = int.MaxValue)
        {
            int snapshot = SnapshotQueuedAppThreadJobs();
            int remaining = Math.Min(Math.Max(0, maxJobs), snapshot);

            int processed = 0;
            while (processed < remaining && TryDequeueWithAging(_pendingAppThreadByPriority, JobAffinity.AppThread, out var job, out var bucket))
            {
                RecordWait(job, bucket);
                using (StartProfilerScope($"AppThreadJobs.{job.Priority}.{job.GetProfilerLabel()}"))
                {
                    ExecuteJob(job);
                }
                processed++;
            }
        }

        public void ProcessCollectVisibleSwapJobs(int maxJobs = 128)
        {
            int processed = 0;
            while (processed < maxJobs && TryDequeueWithAging(_pendingCollectVisibleSwapByPriority, JobAffinity.CollectVisibleSwap, out var job, out var bucket))
            {
                RecordWait(job, bucket);
                ExecuteJob(job);
                processed++;
            }
        }

        private static int? ReadWorkerCountFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.JobWorkers);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private static int? ReadWorkerCapFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.JobWorkerCap);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private static int? ReadQueueLimitFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.JobQueueLimit);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private static int? ReadQueueWarningThresholdFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.JobQueueWarn);
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private sealed class RemoteDispatchJob : Job
        {
            private readonly RemoteJobRequest _request;
            private readonly IRemoteJobTransport _transport;
            private readonly TaskCompletionSource<RemoteJobResponse> _result;

            internal RemoteDispatchJob(
                RemoteJobRequest request,
                IRemoteJobTransport transport,
                TaskCompletionSource<RemoteJobResponse> result)
            {
                _request = request;
                _transport = transport;
                _result = result;
                CallbackContext = null;
                Canceled += OnCanceled;
            }

            public override IEnumerable Process()
            {
                yield return (Func<Task>)(async () =>
                {
                    CancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var response = await _transport.SendAsync(_request, CancellationToken).ConfigureAwait(false);
                        SetResult(response);
                        _result.TrySetResult(response);
                    }
                    catch (OperationCanceledException)
                    {
                        _result.TrySetCanceled(CancellationToken);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _result.TrySetException(ex);
                        throw;
                    }
                });
            }

            private void OnCanceled(Job job)
                => _result.TrySetCanceled();

            internal void CancelResultForShutdown()
                => _result.TrySetCanceled();
        }

        public bool Shutdown(bool waitForWorkers = true)
            => Shutdown(waitForWorkers, ShutdownTaskWaitTimeout);

        internal bool Shutdown(bool waitForWorkers, TimeSpan timeout)
        {
            long deadline = CreateShutdownDeadline(timeout);
            bool firstRequest;
            lock (_submissionSync)
                firstRequest = Interlocked.Exchange(ref _shutdownState, 1) == 0;
            if (!firstRequest && !waitForWorkers)
                return false;

            if (firstRequest)
            {
                Log($"JobManager shutdown requested. waitForWorkers={waitForWorkers}. {CreateShutdownSummary()}");
                CancelOutstandingJobsForShutdown();

                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _generalDomain?.Shutdown(waitForWorkers: false);

                try
                {
                    _remoteReadySignal.Release();
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    _deferredReadySignal.Release();
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (!waitForWorkers)
            {
                Log($"JobManager fast shutdown signaled workers and is returning without joins. {CreateShutdownSummary()}");
                return false;
            }

            if (!Monitor.TryEnter(_shutdownJoinLock, GetShutdownRemaining(deadline)))
                return false;

            try
            {
                if (Volatile.Read(ref _shutdownSynchronizationDisposed) != 0)
                    return true;

                bool generalWorkersStopped = _generalDomain?.Shutdown(
                    waitForWorkers: true,
                    GetShutdownRemaining(deadline)) ?? true;
                bool remoteWorkerStopped = true;
                if (_remoteWorkerTask is { IsCompleted: false } task)
                    remoteWorkerStopped = WaitForTaskShutdown(task, GetShutdownRemaining(deadline));

                bool deferredWorkerStopped = true;
                if (_deferredWorkerTask is { IsCompleted: false } deferred)
                    deferredWorkerStopped = WaitForTaskShutdown(deferred, GetShutdownRemaining(deadline));

                bool submissionsStopped = WaitForSubmissions(GetShutdownRemaining(deadline));
                bool activeJobsStopped = WaitForActiveJobs(GetShutdownRemaining(deadline));

                bool executionStopped = generalWorkersStopped && remoteWorkerStopped &&
                    deferredWorkerStopped && submissionsStopped && activeJobsStopped;
                if (!executionStopped)
                {
                    if (!generalWorkersStopped)
                        Log("JobManager shutdown timed out waiting for the shared general worker domain.");

                    if (!remoteWorkerStopped)
                        Log("JobManager shutdown timed out waiting for the remote dispatch worker task.");

                    if (!deferredWorkerStopped)
                        Log("JobManager shutdown timed out waiting for the deferred enqueue worker task.");

                    if (!submissionsStopped)
                        Log("JobManager shutdown timed out waiting for an admitted Schedule call.");

                    if (!activeJobsStopped)
                        Log("JobManager shutdown timed out waiting for active jobs or pending async continuations.");

                    List<string> activeJobs = SnapshotActiveJobDescriptions();
                    if (activeJobs.Count > 0)
                    {
                        Log("Active jobs still running during shutdown:");
                        foreach (string activeJob in activeJobs)
                            Log($"  {activeJob}");
                    }

                    Log($"JobManager shutdown proceeding without blocking indefinitely. {CreateShutdownSummary()}");
                    return false;
                }

                // Workers are terminal and admission is closed, so a final drain
                // releases any queue entry that raced the first cancellation pass.
                CancelOutstandingJobsForShutdown();
                if (!WaitForShutdownFinalizations(GetShutdownRemaining(deadline)))
                {
                    Log("JobManager shutdown timed out waiting for queued-job cancellation notifications.");
                    return false;
                }

                if (Interlocked.Exchange(ref _shutdownSynchronizationDisposed, 1) != 0)
                    return true;

                _remoteReadySignal.Dispose();
                _deferredReadySignal.Dispose();
                _activeJobsEmpty.Dispose();
                _submissionsEmpty.Dispose();
                _shutdownFinalizationsEmpty.Dispose();
                _cts.Dispose();
                _queueSlots?.Dispose();
                return true;
            }
            finally
            {
                Monitor.Exit(_shutdownJoinLock);
            }
        }

        private void CancelOutstandingJobsForShutdown()
        {
            List<Job> activeJobs;
            lock (_activeLock)
                activeJobs = [.. _active];

            Job? currentJob = _currentExecutingJob;
            foreach (Job job in activeJobs)
            {
                if (ReferenceEquals(job, currentJob))
                    continue;

                Task cancellation = job.RequestCancellationForShutdown();
                if (job.TryClaimShutdownCancellationTracking())
                    TrackShutdownOperation(cancellation);
            }

            CancelQueuedJobsForShutdown(_pendingByPriority, JobAffinity.Any);
            CancelQueuedJobsForShutdown(_pendingMainThreadByPriority, JobAffinity.RenderThread);
            CancelQueuedJobsForShutdown(_pendingAppThreadByPriority, JobAffinity.AppThread);
            CancelQueuedJobsForShutdown(_pendingCollectVisibleSwapByPriority, JobAffinity.CollectVisibleSwap);
            CancelQueuedJobsForShutdown(_pendingRemoteByPriority, JobAffinity.Remote);

            while (_deferredBySlot.TryDequeue(out Job? job))
                CancelQueuedJobForShutdown(job);
        }

        private void CancelQueuedJobsForShutdown(ConcurrentQueue<Job>[] queues, JobAffinity affinity)
        {
            for (int bucket = 0; bucket < queues.Length; bucket++)
            {
                while (queues[bucket].TryDequeue(out Job? job))
                {
                    DecrementCounts(affinity, bucket);
                    CancelQueuedJobForShutdown(job);
                }
            }
        }

        private void CancelQueuedJobForShutdown(Job job)
        {
            bool tracked;
            lock (_shutdownFinalizationLock)
            {
                tracked = Volatile.Read(ref _shutdownFinalizationAdmissionClosed) == 0 &&
                    Volatile.Read(ref _shutdownSynchronizationDisposed) == 0;
                if (tracked)
                {
                    if (_shutdownFinalizerTask is null || _shutdownFinalizerTask.IsCompleted)
                    {
                        try
                        {
                            _shutdownFinalizerTask = Task.Factory.StartNew(
                                ShutdownFinalizationLoop,
                                CancellationToken.None,
                                TaskCreationOptions.LongRunning,
                                TaskScheduler.Default);
                        }
                        catch (Exception dispatchException)
                        {
                            Environment.FailFast(
                                "Unable to start the owned shutdown-cancellation finalizer before publishing queued work.",
                                dispatchException);
                        }
                    }

                    if (_shutdownFinalizationCount++ == 0)
                        _shutdownFinalizationsEmpty.Reset();

                    _shutdownFinalizationQueue.Enqueue(job);
                }
            }

            if (!tracked)
            {
                try
                {
                    _ = Task.Run(() => BeginQueuedJobFinalization(job, tracked: false));
                }
                catch (Exception exception)
                {
                    Environment.FailFast(
                        "Unable to dispatch terminal cancellation for a job rejected after scheduler shutdown.",
                        exception);
                }
            }
        }

        private void ShutdownFinalizationLoop()
        {
            while (true)
            {
                Job? job;
                lock (_shutdownFinalizationLock)
                {
                    if (_shutdownFinalizationQueue.Count == 0)
                    {
                        _shutdownFinalizerTask = null;
                        return;
                    }

                    job = _shutdownFinalizationQueue.Dequeue();
                }

                BeginQueuedJobFinalization(job, tracked: true);
            }
        }

        private void BeginQueuedJobFinalization(Job job, bool tracked)
        {
            Task finalization;
            try
            {
                finalization = job.CompleteCancellationForShutdownAsync();
            }
            catch (Exception exception)
            {
                finalization = Task.FromException(exception);
            }

            if (finalization.IsCompleted)
            {
                CompleteQueuedJobFinalization(finalization, job, tracked);
                return;
            }

            _ = finalization.ContinueWith(
                completed => CompleteQueuedJobFinalization(completed, job, tracked),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteQueuedJobFinalization(Task finalization, Job job, bool tracked)
        {
            try
            {
                ObserveShutdownOperation(finalization);
                if (job is RemoteDispatchJob remote)
                    remote.CancelResultForShutdown();
            }
            finally
            {
                RemoveActive(job);
                if (tracked)
                    CompleteTrackedShutdownOperation();
            }
        }

        private void CancelActiveJobForShutdown(Job job)
        {
            if (!job.TryClaimShutdownManagerFinalization())
                return;

            Task finalization = job.CompleteCancellationForShutdownAsync();
            if (finalization.IsCompleted)
            {
                CompleteActiveJobFinalization(finalization, job);
                return;
            }

            _ = finalization.ContinueWith(
                completed => CompleteActiveJobFinalization(completed, job),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteActiveJobFinalization(Task finalization, Job job)
        {
            try
            {
                ObserveShutdownOperation(finalization);
                if (job is RemoteDispatchJob remote)
                    remote.CancelResultForShutdown();
            }
            finally
            {
                RemoveActive(job);
            }
        }

        private void TrackShutdownOperation(Task operation)
        {
            lock (_shutdownFinalizationLock)
            {
                if (Volatile.Read(ref _shutdownFinalizationAdmissionClosed) != 0 ||
                    Volatile.Read(ref _shutdownSynchronizationDisposed) != 0)
                    return;

                if (_shutdownFinalizationCount++ == 0)
                    _shutdownFinalizationsEmpty.Reset();
            }

            if (operation.IsCompleted)
            {
                ObserveShutdownOperation(operation);
                CompleteTrackedShutdownOperation();
                return;
            }

            _ = operation.ContinueWith(
                completed =>
                {
                    ObserveShutdownOperation(completed);
                    CompleteTrackedShutdownOperation();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteTrackedShutdownOperation()
        {
            lock (_shutdownFinalizationLock)
            {
                if (--_shutdownFinalizationCount == 0)
                    _shutdownFinalizationsEmpty.Set();
            }
        }

        private static void ObserveShutdownOperation(Task operation)
        {
            if (!operation.IsFaulted)
                return;

            _ = operation.Exception;
        }

        private void ReleaseQueueSlot(Job job)
        {
            if (_queueSlots is null || !job.TryClearQueueSlot())
                return;

            if (Volatile.Read(ref _shutdownSynchronizationDisposed) != 0)
                return;

            try
            {
                _queueSlots.Release();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }

            job.UsesQueueSlot = false;
        }

        private static bool WaitForTaskShutdown(Task task, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                return task.IsCompleted;

            try
            {
                return task.Wait(timeout);
            }
            catch (AggregateException)
            {
                return task.IsCompleted;
            }
        }

        private bool WaitForActiveJobs(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                return _activeJobsEmpty.IsSet;

            try
            {
                return _activeJobsEmpty.Wait(timeout);
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private bool WaitForSubmissions(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                return _submissionsEmpty.IsSet;

            try
            {
                return _submissionsEmpty.Wait(timeout);
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private bool WaitForShutdownFinalizations(TimeSpan timeout)
        {
            long deadline = CreateShutdownDeadline(timeout);
            while (true)
            {
                if (!_shutdownFinalizationsEmpty.Wait(GetShutdownRemaining(deadline)))
                    return false;

                lock (_shutdownFinalizationLock)
                {
                    if (_shutdownFinalizationCount != 0)
                        continue;

                    Volatile.Write(ref _shutdownFinalizationAdmissionClosed, 1);
                    return true;
                }
            }
        }

        private static long CreateShutdownDeadline(TimeSpan timeout)
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

        private static TimeSpan GetShutdownRemaining(long deadline)
        {
            long ticks = deadline - Stopwatch.GetTimestamp();
            return ticks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
        }

        private string CreateShutdownSummary()
        {
            int activeJobs;
            lock (_activeLock)
                activeJobs = _active.Count;

            int anyQueued = 0;
            int renderQueued = 0;
            int appQueued = 0;
            int collectQueued = 0;
            int remoteQueued = 0;

            for (int i = 0; i < PriorityLevels; i++)
            {
                anyQueued += Math.Max(0, Volatile.Read(ref _pendingCounts[i]));
                renderQueued += Math.Max(0, Volatile.Read(ref _pendingMainThreadCounts[i]));
                appQueued += Math.Max(0, Volatile.Read(ref _pendingAppThreadCounts[i]));
                collectQueued += Math.Max(0, Volatile.Read(ref _pendingCollectCounts[i]));
                remoteQueued += Math.Max(0, Volatile.Read(ref _pendingRemoteCounts[i]));
            }

            return $"active={activeJobs}, submissions={Volatile.Read(ref _activeSubmissionCount)}, " +
                $"queued(any={anyQueued}, render={renderQueued}, app={appQueued}, collect={collectQueued}, remote={remoteQueued}, deferred={_deferredBySlot.Count})";
        }

        private List<string> SnapshotActiveJobDescriptions()
        {
            List<string> descriptions = [];
            lock (_activeLock)
            {
                foreach (Job job in _active)
                    descriptions.Add(DescribeJob(job));
            }

            return descriptions;
        }

        private static string DescribeJob(Job job)
        {
            string label;
            try
            {
                label = job.GetProfilerLabel();
            }
            catch
            {
                label = job.GetType().Name;
            }

            string pendingTaskStatus = job.PendingTask?.Status.ToString() ?? "none";
            return $"{job.Id} [{job.Affinity}/{job.Priority}] {label} canceled={job.IsCancellationRequested} completed={job.IsCompleted} pendingTask={pendingTaskStatus}";
        }
    }
}
