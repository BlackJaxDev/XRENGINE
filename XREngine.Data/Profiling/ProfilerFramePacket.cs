using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>
/// Full profiler frame snapshot sent at ~30 Hz.
/// Mirrors Engine.CodeProfiler.ProfilerFrameSnapshot.
/// </summary>
[MemoryPackable]
public sealed partial class ProfilerFramePacket
{
    /// <summary>Engine time at which this snapshot was captured.</summary>
    public float FrameTime { get; set; }

    /// <summary>Per-thread profiler data.</summary>
    public ProfilerThreadData[] Threads { get; set; } = [];

    /// <summary>Rolling history of per-thread total-time samples (threadId → float[]).</summary>
    public Dictionary<int, float[]> ThreadHistory { get; set; } = [];

    /// <summary>Per-component timings captured from the latest update tick.</summary>
    public ProfilerComponentTimingData[] ComponentTimings { get; set; } = [];
}

/// <summary>
/// Per-thread profiler tree for a single snapshot.
/// </summary>
[MemoryPackable]
public sealed partial class ProfilerThreadData
{
    public int ThreadId { get; set; }
    public float TotalTimeMs { get; set; }
    public ProfilerNodeData[] RootNodes { get; set; } = [];
}

/// <summary>
/// Single node in the profiler call tree.
/// </summary>
[MemoryPackable]
public sealed partial class ProfilerNodeData
{
    public string Name { get; set; } = string.Empty;
    public float ElapsedMs { get; set; }
    public ProfilerNodeData[] Children { get; set; } = [];
}

/// <summary>
/// Single component timing entry captured during an update tick.
/// </summary>
[MemoryPackable]
public sealed partial class ProfilerComponentTimingData
{
    public Guid ComponentId { get; set; }
    public string ComponentName { get; set; } = string.Empty;
    public string ComponentType { get; set; } = string.Empty;
    public string SceneNodeName { get; set; } = string.Empty;
    public float ElapsedMs { get; set; }
    public int CallCount { get; set; }
    public int TickGroupMask { get; set; }
}
