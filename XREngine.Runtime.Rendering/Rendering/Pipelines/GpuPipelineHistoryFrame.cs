namespace XREngine.Rendering;

/// <summary>Contains one retained GPU frame without its timing tree.</summary>
public readonly record struct GpuPipelineHistoryFrame(
    ulong FrameId,
    DateTimeOffset Timestamp,
    double PipelineMilliseconds,
    double RenderThreadMilliseconds,
    double RenderThreadUnattributedMilliseconds,
    int TimerSampleCount,
    string HottestPath = "",
    double HottestPathMilliseconds = 0);
