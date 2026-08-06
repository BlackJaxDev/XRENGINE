using System.Runtime.CompilerServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-independent owner for command scheduling, recording admission, and
/// persistent schedule artifacts. Native command execution remains supplied by
/// the facade at the call boundary.
/// </summary>
internal sealed class VulkanCommandRuntime
{
    private CommandChainSchedule?[]? _scheduleCache;
    private readonly Dictionary<Type, object> _threadWorkspaces = [];
    private readonly object _threadWorkspacesGate = new();

    internal VulkanProducerCompleteIndirectStream? PendingProducerCompleteIndirectStream { get; set; }
    internal bool ThreadLocalScratchDisposed { get; set; }

    public VulkanCommandScheduler Scheduler { get; } = new();
    public VulkanCommandRecorder Recorder { get; } = new();
    public VulkanCommandWorkerSynchronization Workers { get; } = new();
    public VulkanCommandPoolAuthority Pools { get; } = new();
    public VulkanCommandChainState CommandChains { get; } = new();
    public VulkanCommandBufferState CommandBuffers { get; } = new();
    public VulkanStateTracker StateTracker { get; } = new();
    public VulkanCommandSynchronizationState Synchronization { get; } = new();

    public CommandChainSchedule? GetReusableSchedule(int slot, int slotCount)
    {
        EnsureScheduleCache(slotCount);
        return (uint)slot < (uint)_scheduleCache!.Length ? _scheduleCache[slot] : null;
    }

    public void CacheSchedule(int slot, int slotCount, CommandChainSchedule schedule)
    {
        EnsureScheduleCache(slotCount);
        if ((uint)slot < (uint)_scheduleCache!.Length)
            _scheduleCache[slot] = schedule;
    }

    public void InvalidateScheduleCache()
    {
        if (_scheduleCache is not null)
            Array.Clear(_scheduleCache);
    }

    public void ReleaseScheduleCache() => _scheduleCache = null;

    public VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>
        GetThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>()
        where TRenderState : class
        where TPlannerState : struct
        where TSwitchingState : class
        where TFrameBuffer : class
        where TReadBuffer : struct
    {
        Type key = typeof(VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>);
        lock (_threadWorkspacesGate)
        {
            if (_threadWorkspaces.TryGetValue(key, out object? workspace))
                return (VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>)workspace;

            var created = new VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>();
            _threadWorkspaces.Add(key, created);
            return created;
        }
    }

    public int ResolveParallelRecordingBucket(
        in VulkanMeshFrameDataRendererFamilyKey rendererFamily,
        int workerCount)
    {
        if (workerCount <= 1)
            return 0;

        int rendererIdentity = RuntimeHelpers.GetHashCode(rendererFamily.Renderer);
        return unchecked((int)((uint)rendererIdentity % (uint)workerCount));
    }

    private void EnsureScheduleCache(int slotCount)
    {
        int count = Math.Max(slotCount, 1);
        if (_scheduleCache is not null && _scheduleCache.Length == count)
            return;

        _scheduleCache = new CommandChainSchedule?[count];
    }
}

internal readonly record struct VulkanProducerCompleteIndirectStream(
    XRDataBuffer IndirectBuffer,
    XRDataBuffer? ParameterBuffer,
    ulong IndirectBufferIdentity,
    ulong ParameterBufferIdentity);

/// <summary>Owns primary and per-thread native command-pool identities.</summary>
internal sealed class VulkanCommandPoolAuthority
{
    internal object Gate { get; } = new();
    internal Dictionary<int, CommandPool> GraphicsByThread { get; } = new();
    internal Dictionary<int, CommandPool> TransferByThread { get; } = new();
    internal CommandPool PrimaryGraphics { get; set; }
    internal CommandPool PrimaryTransfer { get; set; }
}

/// <summary>Persistent worker synchronization state, isolated from renderer-owned recording logic.</summary>
internal sealed class VulkanCommandWorkerSynchronization
{
    internal object Gate { get; } = new();
    internal ManualResetEventSlim Idle { get; } = new(initialState: true);
    internal CountdownEvent Countdown { get; } = new(initialCount: 1);
    internal int Generation;
    internal int ActiveWorkerCount;
    internal int Faulted;
    internal VulkanCommandChainRecordingBatch Batch { get; set; } = new();
    internal CommandChainRecordingWorkerState[]? WorkerStates { get; set; }
}
