using System.Collections;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering;

/// <summary>
/// An async semaphore that grants permits in priority order. The capacity can change while permits are held: a larger
/// capacity grants waiters at once, and a smaller capacity takes effect as the held permits are released.
/// </summary>
internal sealed class PriorityAsyncSemaphore
{
    private sealed class Waiter(JobPriority priority)
    {
        private const int Pending = 0;
        private const int Granted = 1;
        private const int Canceled = 2;

        private int _state = Pending;

        public readonly JobPriority Priority = priority;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration;

        public bool IsCanceled => Volatile.Read(ref _state) == Canceled;

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, Canceled, Pending) == Pending)
                Completion.TrySetResult(false);
        }

        public bool TryGrant()
        {
            if (Interlocked.CompareExchange(ref _state, Granted, Pending) != Pending)
                return false;

            CancellationRegistration.Dispose();
            Completion.TrySetResult(true);
            return true;
        }
    }

    private readonly Queue<Waiter>[] _queues;
    private readonly object _sync = new();
    private int _capacity;
    private int _inUse;

    public PriorityAsyncSemaphore(int initialCount)
    {
        if (initialCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCount), "Initial count must be positive.");

        _capacity = initialCount;
        _queues = new Queue<Waiter>[(int)JobPriority.Highest + 1];
        for (int i = 0; i < _queues.Length; i++)
            _queues[i] = new Queue<Waiter>();
    }

    /// <summary>The number of permits that can be held at the same time.</summary>
    public int Capacity => Volatile.Read(ref _capacity);

    /// <summary>The number of permits that are held now.</summary>
    public int InUse => Volatile.Read(ref _inUse);

    /// <summary>
    /// Changes the number of permits that can be held at the same time. It does not take back held permits.
    /// </summary>
    /// <param name="capacity">The new capacity. It must be positive.</param>
    public void SetCapacity(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

        lock (_sync)
        {
            if (_capacity == capacity)
                return;

            Volatile.Write(ref _capacity, capacity);
            GrantWaitersNoLock();
        }
    }

    /// <summary>
    /// Waits for a permit without representing ordinary request cancellation as a thrown exception.
    /// </summary>
    /// <returns><see langword="true"/> when a permit was granted; otherwise, <see langword="false"/>.</returns>
    public ValueTask<bool> WaitAsync(JobPriority priority, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(false);

        lock (_sync)
        {
            if (_inUse < _capacity && !HasWaiters())
            {
                Volatile.Write(ref _inUse, _inUse + 1);
                return ValueTask.FromResult(true);
            }

            Waiter waiter = new(NormalizePriority(priority));
            waiter.CancellationRegistration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(), waiter);
            _queues[(int)waiter.Priority].Enqueue(waiter);
            return new ValueTask<bool>(waiter.Completion.Task);
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            Volatile.Write(ref _inUse, Math.Max(0, _inUse - 1));
            GrantWaitersNoLock();
        }
    }

    private void GrantWaitersNoLock()
    {
        while (_inUse < _capacity && TryDequeueNextWaiter(out Waiter? waiter))
        {
            if (waiter is null)
                continue;

            if (waiter.TryGrant())
            {
                Volatile.Write(ref _inUse, _inUse + 1);
                continue;
            }

            waiter.CancellationRegistration.Dispose();
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
