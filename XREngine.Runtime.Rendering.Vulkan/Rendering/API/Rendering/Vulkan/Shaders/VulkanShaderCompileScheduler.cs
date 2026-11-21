using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Runs Vulkan shader CPU artifact builds (source rewrite, artifact cache lookup,
/// and SPIR-V compilation) on a small set of BelowNormal threads.
/// </summary>
/// <remarks>
/// The .NET thread pool runs at Normal priority and adds threads while compiles
/// block. At startup, hundreds of shader builds could then fill every allowed core
/// and make the whole PC slow. A fixed lane count keeps cores free for other
/// processes. BelowNormal priority lets the foreground engine loops and other
/// processes run first. A queued build must not wait on another build in this
/// scheduler, because the lane count is fixed.
/// </remarks>
internal sealed class VulkanShaderCompileScheduler : TaskScheduler
{
    private const int MinimumLaneCount = 2;
    private const int MaximumLaneCount = 6;

    public static VulkanShaderCompileScheduler Instance { get; } = new();

    private readonly ConcurrentQueue<Task> _queue = new();
    private readonly SemaphoreSlim _queuedWork = new(0);
    private readonly int _laneCount;
    private int _startedLaneCount;

    private VulkanShaderCompileScheduler()
        => _laneCount = Math.Clamp(Environment.ProcessorCount / 4, MinimumLaneCount, MaximumLaneCount);

    public override int MaximumConcurrencyLevel => _laneCount;

    /// <summary>Starts one shader build on a compile lane.</summary>
    public static Task<T> Run<T>(Func<T> build)
        => Task.Factory.StartNew(
            build,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            Instance);

    protected override void QueueTask(Task task)
    {
        _queue.Enqueue(task);
        EnsureLanesStarted();
        _queuedWork.Release();
    }

    // Inline execution would move a build onto the caller thread, which can be a
    // foreground engine loop. Only compile lanes run builds.
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        => false;

    protected override IEnumerable<Task> GetScheduledTasks()
        => _queue.ToArray();

    private void EnsureLanesStarted()
    {
        if (Volatile.Read(ref _startedLaneCount) == _laneCount)
            return;

        lock (_queue)
        {
            while (_startedLaneCount < _laneCount)
            {
                var lane = new Thread(LaneMain)
                {
                    IsBackground = true,
                    Name = $"XRE Vulkan Shader Compile {_startedLaneCount}",
                    Priority = ThreadPriority.BelowNormal,
                };
                lane.Start();
                Volatile.Write(ref _startedLaneCount, _startedLaneCount + 1);
            }
        }
    }

    private void LaneMain()
    {
        while (true)
        {
            _queuedWork.Wait();
            if (_queue.TryDequeue(out Task? task))
                TryExecuteTask(task);
        }
    }
}
