namespace XREngine.Editor;

/// <summary>Contains the display text for one realized profiler row.</summary>
internal readonly record struct NativeProfilerDisplayRow(
    string Scope, string Total, string Self, int SettingIndex = -1,
    long OwnerRevision = 0, string? PipelineName = null,
    ulong GpuWorstFrameId = 0, long GpuHistoryGeneration = 0,
    int GpuRankMetric = -1, int GpuRankPageAction = 0,
    long GpuRankRevision = 0);
