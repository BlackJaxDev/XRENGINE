using System.Collections;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering;

internal sealed class PriorityAsyncSemaphore
{
    private sealed class Waiter(JobPriority priority, CancellationToken cancellationToken)
    {
        private int _canceled;

        public readonly JobPriority Priority = priority;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration;

        public bool IsCanceled => Volatile.Read(ref _canceled) != 0;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _canceled, 1) == 0)
                Completion.TrySetCanceled(cancellationToken);
        }

        public bool TryGrant()
        {
            if (IsCanceled)
                return false;

            CancellationRegistration.Dispose();
            return Completion.TrySetResult();
        }
    }

    private readonly Queue<Waiter>[] _queues;
    private readonly object _sync = new();
    private int _availableCount;

    public PriorityAsyncSemaphore(int initialCount)
    {
        if (initialCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCount), "Initial count must be positive.");

        _availableCount = initialCount;
        _queues = new Queue<Waiter>[(int)JobPriority.Highest + 1];
        for (int i = 0; i < _queues.Length; i++)
            _queues[i] = new Queue<Waiter>();
    }

    public Task WaitAsync(JobPriority priority, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        lock (_sync)
        {
            if (_availableCount > 0 && !HasWaiters())
            {
                _availableCount--;
                return Task.CompletedTask;
            }

            Waiter waiter = new(NormalizePriority(priority), cancellationToken);
            waiter.CancellationRegistration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(), waiter);
            _queues[(int)waiter.Priority].Enqueue(waiter);
            return waiter.Completion.Task;
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            while (TryDequeueNextWaiter(out Waiter? waiter))
            {
                if (waiter is null)
                    continue;

                if (waiter.TryGrant())
                    return;

                waiter.CancellationRegistration.Dispose();
            }

            _availableCount++;
        }
    }

    private bool HasWaiters()
    {
        for (int i = _queues.Length - 1; i >= 0; i--)
        {
            if (_queues[i].Count > 0)
                return true;
        }

        return false;
    }

    private bool TryDequeueNextWaiter(out Waiter? waiter)
    {
        for (int i = _queues.Length - 1; i >= 0; i--)
        {
            Queue<Waiter> queue = _queues[i];
            while (queue.Count > 0)
            {
                Waiter candidate = queue.Dequeue();
                if (candidate.IsCanceled)
                {
                    candidate.CancellationRegistration.Dispose();
                    continue;
                }

                waiter = candidate;
                return true;
            }
        }

        waiter = null;
        return false;
    }

    private static JobPriority NormalizePriority(JobPriority priority)
        => priority < JobPriority.Lowest
            ? JobPriority.Lowest
            : priority > JobPriority.Highest
                ? JobPriority.Highest
                : priority;
}
