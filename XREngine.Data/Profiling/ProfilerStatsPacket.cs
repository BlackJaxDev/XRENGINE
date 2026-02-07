using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>
/// Per-frame rendering statistics snapshot.
/// Mirrors Engine.Rendering.Stats.
/// </summary>
[MemoryPackable]
public sealed partial class RenderStatsPacket
{
    // Draw calls
    public int DrawCalls { get; set; }
    public int MultiDrawCalls { get; set; }
    public int TrianglesRendered { get; set; }

    // VRAM
    public long AllocatedVRAMBytes { get; set; }
    public long AllocatedBufferBytes { get; set; }
    public long AllocatedTextureBytes { get; set; }
    public long AllocatedRenderBufferBytes { get; set; }

    // FBO bandwidth
    public long FBOBandwidthBytes { get; set; }
    public int FBOBindCount { get; set; }

    // Render matrix
    public bool RenderMatrixStatsReady { get; set; }
    public int RenderMatrixApplied { get; set; }
    public int RenderMatrixSetCalls { get; set; }
    public int RenderMatrixListenerInvocations { get; set; }
    public RenderMatrixListenerEntry[] RenderMatrixListenerCounts { get; set; } = [];

    // Octree
    public bool OctreeStatsReady { get; set; }
    public int OctreeAddCount { get; set; }
    public int OctreeMoveCount { get; set; }
    public int OctreeRemoveCount { get; set; }
    public int OctreeSkippedMoveCount { get; set; }
}

/// <summary>
/// A single render-matrix listener type and its invocation count.
/// Replaces KeyValuePair&lt;string, int&gt; for MemoryPack compatibility.
/// </summary>
[MemoryPackable]
public sealed partial class RenderMatrixListenerEntry
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// Per-thread GC allocation tracking snapshot.
/// Mirrors Engine.ThreadAllocationSnapshot.
/// </summary>
[MemoryPackable]
public sealed partial class ThreadAllocationsPacket
{
    public AllocationSlice Render { get; set; } = new();
    public AllocationSlice CollectSwap { get; set; } = new();
    public AllocationSlice Update { get; set; } = new();
    public AllocationSlice FixedUpdate { get; set; } = new();
}

/// <summary>
/// Snapshot of one allocation tracking ring buffer.
/// Mirrors Engine.AllocationRingSnapshot.
/// </summary>
[MemoryPackable]
public sealed partial class AllocationSlice
{
    public long LastBytes { get; set; }
    public double AverageBytes { get; set; }
    public long MaxBytes { get; set; }
    public int Samples { get; set; }
    public int Capacity { get; set; }

    public double LastKB => LastBytes / 1024.0;
    public double AverageKB => AverageBytes / 1024.0;
    public double MaxKB => MaxBytes / 1024.0;
}

/// <summary>
/// GPU BVH profiling metrics snapshot.
/// Mirrors BvhGpuProfiler.Metrics.
/// </summary>
[MemoryPackable]
public sealed partial class BvhMetricsPacket
{
    public uint BuildCount { get; set; }
    public double BuildMilliseconds { get; set; }
    public uint RefitCount { get; set; }
    public double RefitMilliseconds { get; set; }
    public uint CullCount { get; set; }
    public double CullMilliseconds { get; set; }
    public uint RaycastCount { get; set; }
    public double RaycastMilliseconds { get; set; }
}

/// <summary>
/// Job system stats snapshot.
/// Mirrors Engine.Jobs (JobManager) state.
/// </summary>
[MemoryPackable]
public sealed partial class JobSystemStatsPacket
{
    public int WorkerCount { get; set; }
    public bool IsQueueBounded { get; set; }
    public int QueueCapacity { get; set; }
    public int QueueSlotsInUse { get; set; }
    public int QueueSlotsAvailable { get; set; }
    public JobPriorityStatsEntry[] Priorities { get; set; } = [];
}

/// <summary>
/// Per-priority job queue stats.
/// </summary>
[MemoryPackable]
public sealed partial class JobPriorityStatsEntry
{
    public int Priority { get; set; }
    public string PriorityName { get; set; } = string.Empty;
    public int QueuedAny { get; set; }
    public int QueuedMain { get; set; }
    public int QueuedCollect { get; set; }
    public double AvgWaitMs { get; set; }
}

/// <summary>
/// Main thread invoke log entries (sent as a delta or full snapshot).
/// </summary>
[MemoryPackable]
public sealed partial class MainThreadInvokesPacket
{
    public MainThreadInvokeEntryData[] Entries { get; set; } = [];
}

/// <summary>
/// A single main-thread invoke log entry.
/// Mirrors Engine.MainThreadInvokeEntry.
/// </summary>
[MemoryPackable]
public sealed partial class MainThreadInvokeEntryData
{
    public long Sequence { get; set; }
    public long TimestampTicks { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int CallerThreadId { get; set; }
}

/// <summary>
/// Heartbeat sent at ~1 Hz to indicate the engine process is alive.
/// </summary>
[MemoryPackable]
public sealed partial class HeartbeatPacket
{
    public string ProcessName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public long UptimeMs { get; set; }
}
