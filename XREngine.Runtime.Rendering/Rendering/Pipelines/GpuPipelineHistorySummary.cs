namespace XREngine.Rendering;

/// <summary>Contains aggregate values for the captured lifetime of one GPU pipeline.</summary>
public readonly record struct GpuPipelineHistorySummary(
    long HistoryGeneration,
    ulong LatestFrameId,
    int FrameCount,
    int RetainedFrameCount,
    int WorstFrameCount,
    long TimerSampleCount,
    double AverageMilliseconds,
    double StandardDeviationMilliseconds,
    double MinimumMilliseconds,
    ulong MinimumFrameId,
    double MaximumMilliseconds,
    ulong MaximumFrameId);
