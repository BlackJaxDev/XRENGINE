namespace XREngine.Data.Profiling;

/// <summary>
/// Abstracts the source of profiler telemetry data so the same UI can render
/// from either an in-process engine (<see cref="ProfilerFramePacket"/> built from
/// <c>Engine.*</c> statics) or a remote UDP stream.
/// </summary>
public interface IProfilerDataSource
{
    // ── Packet snapshots (nullable = not yet available) ──

    ProfilerFramePacket? LatestFrame { get; }
    RenderStatsPacket? LatestRenderStats { get; }
    ThreadAllocationsPacket? LatestAllocations { get; }
    BvhMetricsPacket? LatestBvhMetrics { get; }
    JobSystemStatsPacket? LatestJobStats { get; }
    MainThreadInvokesPacket? LatestMainThreadInvokes { get; }
    HeartbeatPacket? LatestHeartbeat { get; }

    // ── Connection status ──

    /// <summary>True when the data source is actively producing data.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Seconds since the last heartbeat / data update.
    /// Return <see cref="double.MaxValue"/> if no data has ever been received.
    /// In-process sources may always return 0.
    /// </summary>
    double SecondsSinceLastHeartbeat { get; }

    // ── Counters (informational — remote sources track these, in-process can return 0) ──

    long PacketsReceived { get; }
    long BytesReceived { get; }
    long ErrorsCount { get; }

    // ── Multi-instance (remote only — in-process returns empty/false) ──

    /// <summary>Returns known engine sources (by heartbeat PID). May be empty.</summary>
    IReadOnlyList<ProfilerSourceInfo> GetKnownSources();

    /// <summary>True when more than one source PID has been seen.</summary>
    bool HasMultipleSources { get; }
}

/// <summary>
/// Describes a known engine instance that has sent heartbeats.
/// Shared DTO so both in-process and remote data sources can provide it.
/// </summary>
public sealed class ProfilerSourceInfo
{
    public string ProcessName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public long UptimeMs { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
