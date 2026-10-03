namespace XREngine.RenderBench;

/// <summary>Adapter and queue-family counter identity returned by VK_KHR_performance_query.</summary>
public sealed record RenderBenchPerformanceCounterMetadata(
    uint Index,
    string Uuid,
    string Name,
    string Category,
    string Description,
    string Unit,
    string Scope,
    string Storage,
    string Flags);
