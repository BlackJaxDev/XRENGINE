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

    /// <summary>
    /// Waits for every registered CPU readback task and fails retirement if the
    /// boundary cannot be proven. The best-effort variant remains appropriate
    /// only after teardown has already committed to cleanup.
    /// </summary>
    public void WaitForPendingTasksOrThrow(TimeSpan timeout)
    {
        Task[] pending;
        lock (_sync)
        {
            if (_pendingTasks.Count == 0)
                return;

            pending = [.. _pendingTasks];
        }

        if (!Task.WaitAll(pending, timeout))
        {
            throw new TimeoutException(
                $"Timed out waiting for {pending.Length} Vulkan readback task(s) during backend retirement.");
        }
    }

    private void Remove(Task completed)
    {
        lock (_sync)
            _pendingTasks.Remove(completed);
    }
}
