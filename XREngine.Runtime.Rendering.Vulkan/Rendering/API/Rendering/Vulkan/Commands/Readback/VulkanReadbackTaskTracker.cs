using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.Vulkan.Commands.Readback;

/// <summary>
/// Owns the asynchronous GPU readback tasks that must settle before renderer teardown.
/// </summary>
internal sealed class VulkanReadbackTaskTracker
{
    private readonly object _sync = new();
    private readonly List<Task> _pendingTasks = [];

    public void Register(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_sync)
            _pendingTasks.Add(task);

        _ = task.ContinueWith(
            static (completed, state) => ((VulkanReadbackTaskTracker)state!).Remove(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void WaitForPendingTasks(TimeSpan timeout)
    {
        Task[] pending;
        lock (_sync)
        {
            if (_pendingTasks.Count == 0)
                return;

            pending = [.. _pendingTasks];
        }

        try
        {
            Task.WaitAll(pending, timeout);
        }
        catch
        {
            // Best-effort shutdown path: lingering readbacks should not abort renderer teardown.
        }
    }

    private void Remove(Task completed)
    {
        lock (_sync)
            _pendingTasks.Remove(completed);
    }
}
