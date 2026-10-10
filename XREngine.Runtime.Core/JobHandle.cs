using System;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine
{
    public readonly struct JobHandle
    {
        private readonly Task? _completion;
        private readonly Job? _job;

        internal JobHandle(Guid id, Task completionTask, Job job)
        {
            JobId = id;
            _completion = completionTask;
            _job = job;
        }

        public Guid JobId { get; }
        public bool IsValid => _completion != null;
        public bool IsCompleted => _completion?.IsCompleted ?? true;
        public bool IsFaulted => _job?.IsFaulted ?? _completion?.IsFaulted ?? false;
        public bool IsCanceled => _job?.IsCanceled ?? _completion?.IsCanceled ?? false;
        public JobPriority Priority => _job?.Priority ?? JobPriority.Normal;
        public Job? Job => _job;

        public void Wait(CancellationToken cancellationToken = default)
        {
            if (_job?.UsesCallerThreadExecutor == true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObserveCallerThreadCompletion();
                return;
            }

            _completion?.Wait(cancellationToken);
        }

        public bool Wait(TimeSpan timeout)
        {
            if (_job?.UsesCallerThreadExecutor == true)
            {
                ObserveCallerThreadCompletion();
                return true;
            }

            return _completion?.Wait(timeout) ?? true;
        }

        private void ObserveCallerThreadCompletion()
        {
            if (_completion is null)
                return;
            if (!_completion.IsCompleted)
            {
                throw new NotSupportedException(
                    "A caller-thread job cannot be synchronously waited on. Await WaitAsync while the host continues pumping jobs.");
            }

            // Completion has been observed, so retrieving a terminal result
            // cannot block the host thread or prevent scheduler progress.
            _completion.GetAwaiter().GetResult();
        }

        public Task WaitAsync(CancellationToken cancellationToken = default)
            => _completion?.WaitAsync(cancellationToken) ?? Task.CompletedTask;

        public bool Cancel()
        {
            _job?.Cancel();
            return _job != null;
        }
    }
}
