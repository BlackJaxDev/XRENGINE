using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Runs a cold Vulkan pipeline compile without occupying a thread-pool thread.
/// One persistent below-normal-priority worker owns the native calls; creating a new
/// OS thread per variant made dense cold views spend seconds scheduling sub-ms
/// driver compiles. Foreground requests take queue priority, while an active
/// native call finishes without holding the render scheduling admission gate.
/// </summary>
internal sealed class VulkanPipelineCompileTask : IDisposable
{
    private abstract class WorkItem
    {
        private int _foregroundRequired;
        private int _executionStarted;

        protected WorkItem(bool foregroundRequired)
            => _foregroundRequired = foregroundRequired ? 1 : 0;

        internal bool IsForegroundRequired
            => Volatile.Read(ref _foregroundRequired) != 0;

        internal bool TryPromoteToForeground()
            => Interlocked.Exchange(ref _foregroundRequired, 1) == 0;

        internal bool TryClaimExecution()
            => Interlocked.CompareExchange(ref _executionStarted, 1, 0) == 0;

        internal void ExecuteClaimed() => ExecuteCore();

        protected abstract void ExecuteCore();
    }

    private sealed class WorkItem<T>(
        Func<T> compile,
        bool foregroundRequired) : WorkItem(foregroundRequired)
    {
        internal readonly Func<T> Compile = compile;
        internal readonly TaskCompletionSource<T> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void ExecuteCore()
            => Execute(this);
    }

    private readonly ConcurrentQueue<WorkItem> _foregroundQueue = new();
    private readonly ConcurrentQueue<WorkItem> _backgroundQueue = new();
    private readonly SemaphoreSlim _queuedWork = new(0);
    private readonly Thread _worker;
    private int _shutdownStarted;

    internal VulkanPipelineCompileTask()
    {
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "XRE Vulkan Pipeline Compile",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    internal Task<T> Enqueue<T>(
        Func<T> compile,
        bool foregroundRequired,
        out Action promoteToForeground)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _shutdownStarted) != 0,
            this);
        var item = new WorkItem<T>(compile, foregroundRequired);
        Enqueue(item);
        promoteToForeground = () => PromoteToForeground(item);
        return item.Completion.Task;
    }

    private void Enqueue(WorkItem item)
    {
        if (item.IsForegroundRequired)
            _foregroundQueue.Enqueue(item);
        else
            _backgroundQueue.Enqueue(item);
        _queuedWork.Release();
    }

    private void PromoteToForeground(WorkItem item)
    {
        if (!item.TryPromoteToForeground())
            return;
        _foregroundQueue.Enqueue(item);
        _queuedWork.Release();
    }

    private static void Execute<T>(WorkItem<T> item)
    {
        try
        {
            item.Completion.TrySetResult(item.Compile());
        }
        catch (Exception exception)
        {
            item.Completion.TrySetException(exception);
        }
    }

    private void WorkerMain()
    {
        while (true)
        {
            _queuedWork.Wait();
            if (!TryDequeue(out WorkItem work))
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                    return;
                continue;
            }

            if (work.IsForegroundRequired)
            {
                if (work.TryClaimExecution())
                    work.ExecuteClaimed();
                continue;
            }

            if (!RenderForegroundWorkCoordinator.TryEnterBackgroundSlice(
                    out RenderForegroundWorkCoordinator.BackgroundSlice backgroundSlice))
            {
                _backgroundQueue.Enqueue(work);
                _queuedWork.Release();
                RenderForegroundWorkCoordinator.WaitForBackgroundPermission();
                continue;
            }

            bool executionClaimed;
            try
            {
                executionClaimed = work.TryClaimExecution();
            }
            finally
            {
                backgroundSlice.Dispose();
            }

            // Admission is bounded. The driver compile cannot be preempted and
            // must not hold the scheduling gate that foreground frames drain.
            if (executionClaimed)
                work.ExecuteClaimed();
        }
    }

    private bool TryDequeue(out WorkItem work)
    {
        if (_foregroundQueue.TryDequeue(out WorkItem? foreground) &&
            foreground is not null)
        {
            work = foreground;
            return true;
        }
        if (_backgroundQueue.TryDequeue(out WorkItem? background) &&
            background is not null)
        {
            work = background;
            return true;
        }
        work = null!;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;
        _queuedWork.Release();
        _worker.Join();
        _queuedWork.Dispose();
    }
}
