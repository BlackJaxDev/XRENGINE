using System.Collections;
using System.Collections.Concurrent;

namespace XREngine
{
    public class JobManager
    {
        private readonly ConcurrentQueue<Job> _pending = new();
        private readonly List<Job> _active = new();
        private readonly object _activeLock = new();

        public IReadOnlyCollection<Job> Active
        {
            get
            {
                lock (_activeLock)
                    return _active.ToArray();
            }
        }

        public void Schedule(Job job)
            => Schedule(job, CancellationToken.None);

        public void Schedule(Job job, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (!job.TryStart())
                throw new InvalidOperationException("Job has already been scheduled or completed.");

            job.LinkCancellationToken(cancellationToken);
            _pending.Enqueue(job);
        }

        public EnumeratorJob Schedule(
            IEnumerable routine,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default)
        {
            var job = new EnumeratorJob(routine, progress, completed, error, canceled, progressWithPayload);
            Schedule(job, cancellationToken);
            return job;
        }

        public EnumeratorJob Schedule(
            Func<IEnumerable> routineFactory,
            Action<float>? progress = null,
            Action? completed = null,
            Action<Exception>? error = null,
            Action? canceled = null,
            Action<float, object?>? progressWithPayload = null,
            CancellationToken cancellationToken = default)
        {
            var job = new EnumeratorJob(routineFactory, progress, completed, error, canceled, progressWithPayload);
            Schedule(job, cancellationToken);
            return job;
        }

        public bool Cancel(Job job)
        {
            if (job is null)
                throw new ArgumentNullException(nameof(job));

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

            foreach (var pending in _pending)
            {
                if (pending.Id == jobId)
                {
                    pending.Cancel();
                    return true;
                }
            }

            return false;
        }

        public bool Process()
        {
            var didWork = false;

            while (_pending.TryDequeue(out var pending))
            {
                lock (_activeLock)
                {
                    _active.Add(pending);
                }
                didWork = true;
            }

            Job[] snapshot;
            lock (_activeLock)
            {
                if (_active.Count == 0)
                    return didWork;

                snapshot = _active.ToArray();
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
    }
}
