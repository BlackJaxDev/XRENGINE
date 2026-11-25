namespace XREngine.Rendering;

/// <summary>Selects a captured GPU node ranking.</summary>
public enum EGpuPipelineNodeRankMetric
{
    WorstFrame,
    AverageActiveFrame,
    TotalCapturedTime,
}
