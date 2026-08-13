using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Runs a cold Vulkan pipeline compile without occupying a thread-pool thread.
/// One persistent below-normal worker owns the native calls; creating a new OS
/// thread per variant made dense cold views spend seconds scheduling sub-ms
/// driver compiles.
/// </summary>
internal sealed class VulkanPipelineCompileTask : IDisposable
{
    private sealed class WorkItem<T>(Func<T> compile)
    {
        internal readonly Func<T> Compile = compile;
        internal readonly TaskCompletionSource<T> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;

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

    internal Task<T> Enqueue<T>(Func<T> compile)
    {
        var item = new WorkItem<T>(compile);
        _queue.Add(() => Execute(item));
        return item.Completion.Task;
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
        foreach (Action work in _queue.GetConsumingEnumerable())
            work();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join();
        _queue.Dispose();
    }
}
