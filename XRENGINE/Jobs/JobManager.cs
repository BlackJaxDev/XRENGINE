using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine
{
    public class JobManager
    {
        [ThreadStatic]
        private static bool _isJobWorkerThread;

        /// <summary>
        /// True when executing on any JobManager worker thread (including remote dispatch worker).
        /// Use this instead of <see cref="Engine.JobThreadId"/>, which only tracks the first worker.
        /// </summary>
        public static bool IsJobWorkerThread => _isJobWorkerThread;

        private const int PriorityLevels = 5; // Matches JobPriority enum
        private const int DefaultWorkerCap = 16;
        private const int DefaultReservedThreads = 4; // render + update + fixed update + collect visible / swap buffers
        private const int DefaultQueueWarningThreshold = 2048;
        private const int DefaultQueueLimit = 8192;
        private const int QueueAcquireWaitMs = 50;
        private static readonly TimeSpan StarvationWarningThreshold = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan BackpressureLogInterval = TimeSpan.FromSeconds(1);
        private static readonly long StarvationWarningTicks = (long)(StarvationWarningThreshold.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        private static readonly TimeSpan RemoteWorkerIdleTimeout = TimeSpan.FromSeconds(30);
        private readonly ConcurrentQueue<Job>[] _pendingByPriority =
        [
            new(), // Lowest
            new(), // Low
            new(), // Normal
            new(), // High
            new(), // Highest
        ];
        private readonly ConcurrentQueue<Job>[] _pendingMainThreadByPriority =
        [
            new(), // Lowest
            new(), // Low
            new(), // Normal
            new(), // High
            new(), // Highest
        ];
        private readonly ConcurrentQueue<Job>[] _pendingCollectVisibleSwapByPriority =
        [
            new(), // Lowest
            new(), // Low
            new(), // Normal
            new(), // High
            new(), // Highest
        ];
        private readonly ConcurrentQueue<Job>[] _pendingRemoteByPriority =
        [
            new(), // Lowest
            new(), // Low
            new(), // Normal
            new(), // High
            new(), // Highest
        ];
        private readonly List<Job> _active = new();
        private readonly object _activeLock = new();

        private readonly int[] _pendingCounts = new int[PriorityLevels];
        private readonly int[] _pendingMainThreadCounts = new int[PriorityLevels];
        private readonly int[] _pendingCollectCounts = new int[PriorityLevels];
        private readonly int[] _pendingRemoteCounts = new int[PriorityLevels];
        private readonly long[] _totalWaitTicks = new long[PriorityLevels];
        private readonly long[] _waitSamples = new long[PriorityLevels];
        private readonly long[] _lastQueueWarningTicks = new long[PriorityLevels];
        private readonly SemaphoreSlim? _queueSlots;
        private readonly int _queueWarningThreshold;
        private readonly int _maxQueueSize;

        private readonly SemaphoreSlim _readySignal = new(0);
        private readonly SemaphoreSlim _remoteReadySignal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread[] _workers;
        private readonly object _remoteWorkerLock = new();
        private Task? _remoteWorkerTask;

        public int WorkerCount => _workers.Length;
        public IRemoteJobTransport? RemoteTransport { get; set; }

        public JobManager(int? workerCount = null, int? maxQueueSize = null, int? queueWarningThreshold = null, int? workerCap = null)
        {
            int cap = workerCap ?? ReadWorkerCapFromEnv() ?? DefaultWorkerCap;
            int reserved = Math.Max(0, DefaultReservedThreads);
            int defaultWorkers = Math.Max(1, Environment.ProcessorCount - reserved);
            defaultWorkers = Math.Min(defaultWorkers, cap);

            int count = workerCount ?? ReadWorkerCountFromEnv() ?? defaultWorkers;
            count = Math.Clamp(count, 1, cap);

            _maxQueueSize = maxQueueSize ?? ReadQueueLimitFromEnv() ?? DefaultQueueLimit;
            _queueWarningThreshold = queueWarningThreshold ?? ReadQueueWarningThresholdFromEnv() ?? DefaultQueueWarningThreshold;
            _queueWarningThreshold = Math.Max(_queueWarningThreshold, PriorityLevels);

            if (_maxQueueSize > 0 && _queueWarningThreshold > _maxQueueSize)
                _queueWarningThreshold = _maxQueueSize;

            if (_maxQueueSize > 0)
                _queueSlots = new SemaphoreSlim(_maxQueueSize, _maxQueueSize);

            _workers = new Thread[count];

            for (int i = 0; i < count; i++)
            {
                _workers[i] = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"XRJobWorker-{i}"
                };
                _workers[i].Start();
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
                JobAffinity.MainThread => Volatile.Read(ref _pendingMainThreadCounts[bucket]),
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

        public JobHandle Schedule(Job job, JobPriority priority, JobAffinity affinity = JobAffinity.Any, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (!job.TryStart())
                throw new InvalidOperationException("Job has already been scheduled or completed.");

            job.Priority = priority;
            job.Affinity = affinity;
            job.LinkCancellationToken(cancellationToken);

            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            job.AttachCompletionSource(completionSource);
            job.UsesQueueSlot = true;

            Enqueue(job, countAgainstSlots: true);
            return job.Handle;
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
            int bucket = Math.Clamp((int)job.Priority, 0, PriorityLevels - 1);

            if (countAgainstSlots)
            {
                AcquireQueueSlot(job.CancellationToken);
                if (_queueSlots != null)
                    job.UsesQueueSlot = true;
            }

            job.MarkQueued(System.Diagnostics.Stopwatch.GetTimestamp());
            IncrementCounts(job.Affinity, bucket);

            switch (job.Affinity)
            {
                case JobAffinity.MainThread:
                    _pendingMainThreadByPriority[bucket].Enqueue(job);
                    break;
                case JobAffinity.CollectVisibleSwap:
                    _pendingCollectVisibleSwapByPriority[bucket].Enqueue(job);
                    break;
                case JobAffinity.Remote:
                    EnsureRemoteWorker();
                    _pendingRemoteByPriority[bucket].Enqueue(job);
                    _remoteReadySignal.Release();
                    break;
                default:
                    _pendingByPriority[bucket].Enqueue(job);
                    _readySignal.Release();
                    break;
            }
        }

        private void AcquireQueueSlot(CancellationToken cancellationToken)
        {
            if (_queueSlots is null)
                return;

            long lastLogTick = System.Diagnostics.Stopwatch.GetTimestamp();

            while (true)
            {
                if (_cts.IsCancellationRequested)
                    return;

                cancellationToken.ThrowIfCancellationRequested();

                if (_queueSlots.Wait(QueueAcquireWaitMs, cancellationToken))
                    return;

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (TicksToTimeSpan(now - lastLogTick) >= BackpressureLogInterval)
                {
                    Debug.Out(EOutputVerbosity.Normal, $"Job queue back-pressure: waiting for free slot (limit {_maxQueueSize}).");
                    lastLogTick = now;
                }
            }
        }

        private void IncrementCounts(JobAffinity affinity, int bucket)
        {
            int newCount = affinity switch
            {
                JobAffinity.MainThread => Interlocked.Increment(ref _pendingMainThreadCounts[bucket]),
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
                case JobAffinity.MainThread:
                    Interlocked.Decrement(ref _pendingMainThreadCounts[bucket]);
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
                Debug.Out(EOutputVerbosity.Normal, $"Job queue [{(JobPriority)bucket}] length {queuedCount} exceeds threshold {_queueWarningThreshold} (cap {_maxQueueSize}).");
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
                Debug.Out(EOutputVerbosity.Normal, $"Job {job.Id} ({job.Priority}) waited {waitMs:F1} ms before execution.");
        }

        private static TimeSpan TicksToTimeSpan(long ticks)
            => TimeSpan.FromSeconds(ticks / (double)System.Diagnostics.Stopwatch.Frequency);

        public bool Process()
        {
            return TryDispatchOnce();
        }

        private void WorkerLoop()
        {
            _isJobWorkerThread = true;
            if (!Engine.JobThreadId.HasValue)
                Engine.JobThreadId = Thread.CurrentThread.ManagedThreadId;

            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _readySignal.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!TryDequeueWithAging(_pendingByPriority, JobAffinity.Any, out var job, out var bucket))
                    continue;

                RecordWait(job, bucket);
                ExecuteJob(job);
            }
        }

        private void RemoteWorkerLoop()
        {
            _isJobWorkerThread = true;
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_remoteReadySignal.Wait(RemoteWorkerIdleTimeout, token))
                    {
                        if (IsRemoteQueueEmpty())
                        {
                            ClearRemoteWorkerTask();
                            return;
                        }

                        // If there are jobs but we timed out, loop again to wait.
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!TryDequeueWithAging(_pendingRemoteByPriority, JobAffinity.Remote, out var job, out var bucket))
                    continue;

                RecordWait(job, bucket);
                ExecuteJob(job);
            }

            ClearRemoteWorkerTask();
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
            lock (_activeLock)
            {
                if (!_active.Contains(job))
                    _active.Add(job);
            }

            const int MaxStepsPerDispatch = 64;
            int steps = 0;

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
                    RemoveActive(job);
                    return;
                }

                switch (result)
                {
                    case JobStepResult.Completed:
                        RemoveActive(job);
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

        private void RemoveActive(Job job)
        {
            lock (_activeLock)
            {
                _active.Remove(job);
            }

            if (job.UsesQueueSlot && _queueSlots != null)
                _queueSlots.Release();
        }

        private void Requeue(Job job)
        {
            Enqueue(job, countAgainstSlots: false);
        }

        private bool TryDispatchOnce()
        {
            if (!TryDequeueWithAging(_pendingByPriority, JobAffinity.Any, out var job, out var bucket))
                return false;

            RecordWait(job, bucket);
            ExecuteJob(job);
            return true;
        }

        private int SnapshotQueuedMainThreadJobs()
        {
            int total = 0;
            for (int i = 0; i < PriorityLevels; i++)
                total += Math.Max(0, Volatile.Read(ref _pendingMainThreadCounts[i]));
            return total;
        }

        /// <summary>
        /// Drains main-thread jobs that were already queued when this method begins.
        /// This method never waits/spins for more work, and it will not chase newly-enqueued jobs.
        /// </summary>
        internal void ProcessMainThreadJobs(int maxJobs = int.MaxValue)
        {
            int snapshot = SnapshotQueuedMainThreadJobs();
            int remaining = Math.Min(Math.Max(0, maxJobs), snapshot);

            int processed = 0;
            while (processed < remaining && TryDequeueWithAging(_pendingMainThreadByPriority, JobAffinity.MainThread, out var job, out var bucket))
            {
                RecordWait(job, bucket);
                ExecuteJob(job);
                processed++;
            }
        }

        internal void ProcessCollectVisibleSwapJobs(int maxJobs = 128)
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
            string? value = Environment.GetEnvironmentVariable("XR_JOB_WORKERS");
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private static int? ReadWorkerCapFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable("XR_JOB_WORKER_CAP");
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private static int? ReadQueueLimitFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable("XR_JOB_QUEUE_LIMIT");
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private static int? ReadQueueWarningThresholdFromEnv()
        {
            string? value = Environment.GetEnvironmentVariable("XR_JOB_QUEUE_WARN");
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
        }

        private sealed class RemoteDispatchJob(RemoteJobRequest request, IRemoteJobTransport transport, TaskCompletionSource<RemoteJobResponse> result) : Job
        {
            private readonly RemoteJobRequest _request = request;
            private readonly IRemoteJobTransport _transport = transport;
            private readonly TaskCompletionSource<RemoteJobResponse> _result = result;

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
        }

        public void Shutdown()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if the CTS has already been disposed
            }
            
            // Wake all workers so they can observe cancellation
            try
            {
                _readySignal.Release(_workers.Length);
            }
            catch (OperationCanceledException)
            {
                // Ignore if the signal is already canceled
            }

            try
            {
                _remoteReadySignal.Release();
            }
            catch (OperationCanceledException)
            {
                // Ignore if the signal is already canceled
            }

            foreach (var worker in _workers)
                if (worker.IsAlive)
                    worker.Join();

            if (_remoteWorkerTask is { IsCompleted: false } task)
            {
                try { task.Wait(); }
                catch (AggregateException) { /* ignore; cancellation or shutdown exceptions */ }
            }

            _readySignal.Dispose();
            _remoteReadySignal.Dispose();
            _cts.Dispose();
            _queueSlots?.Dispose();
        }
    }
}
