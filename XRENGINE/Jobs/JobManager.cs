using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace XREngine
{
    public class JobManager
    {
        private const int PriorityLevels = 5; // Matches JobPriority enum
        private readonly ConcurrentQueue<Job>[] _pendingByPriority =
        [
            new(), // Lowest
            new(), // Low
            new(), // Normal
            new(), // High
            new(), // Highest
        ];
        private readonly List<Job> _active = new();
        private readonly object _activeLock = new();

        public IReadOnlyCollection<Job> Active
        {
            get
            {
                lock (_activeLock)
                    return [.. _active];
            }
        }

        public void Schedule(Job job)
            => Schedule(job, JobPriority.Normal, CancellationToken.None);

        public void Schedule(Job job, CancellationToken cancellationToken)
            => Schedule(job, JobPriority.Normal, cancellationToken);

        public void Schedule(Job job, JobPriority priority, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (!job.TryStart())
                throw new InvalidOperationException("Job has already been scheduled or completed.");

            job.Priority = priority;
            job.LinkCancellationToken(cancellationToken);

            int bucket = Math.Clamp((int)priority, 0, PriorityLevels - 1);
            _pendingByPriority[bucket].Enqueue(job);
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
            Schedule(job, priority, cancellationToken);
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
            Schedule(job, priority, cancellationToken);
            return job;
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

            return false;
        }

        public bool Process()
        {
            var didWork = false;

            for (int p = PriorityLevels - 1; p >= 0; p--)
            {
                var queue = _pendingByPriority[p];
                while (queue.TryDequeue(out var pending))
                {
                    lock (_activeLock)
                    {
                        _active.Add(pending);
                    }
                    didWork = true;
                }
            }

            Job[] snapshot;
            lock (_activeLock)
            {
                if (_active.Count == 0)
                    return didWork;

                var highestPriority = GetHighestActivePriorityUnsafe();
                var topTier = new List<Job>(_active.Count);
                foreach (var job in _active)
                {
                    if (job.Priority == highestPriority)
                        topTier.Add(job);
                }

                snapshot = [.. topTier];
            }

            foreach (var job in snapshot)
            {
                var result = job.Step();
                if (result == JobStepResult.Completed)
                {
                    lock (_activeLock)
                    {
                        _active.Remove(job);
                    }
                    didWork = true;
                }
                else if (result == JobStepResult.Progressed)
                {
                    didWork = true;
                }
            }

            return didWork;
        }

        private JobPriority GetHighestActivePriorityUnsafe()
        {
            JobPriority highest = JobPriority.Lowest;
            foreach (var job in _active)
            {
                if (job.Priority > highest)
                    highest = job.Priority;
            }

            return highest;
        }
    }
}
