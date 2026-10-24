namespace XREngine.Rendering;

/// <summary>Contains captured values for one ranked GPU node path.</summary>
public readonly record struct GpuPipelineNodeRankRow(
    string Path, string Name, int Depth, bool ShaderOrMaterialHint,
    int ObservedFrameCount, long TimerSampleCount,
    double TotalMilliseconds, double AverageActiveFrameMilliseconds,
    double AverageTimerSampleMilliseconds, double MinimumMilliseconds,
    double MaximumMilliseconds, ulong WorstFrameId,
    double LastMilliseconds, ulong LastFrameId);
