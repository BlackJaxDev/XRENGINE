namespace XREngine.RenderBench;

/// <summary>One selected hardware counter with its reported unit, storage, and result.</summary>
public sealed record RenderBenchPerformanceCounterValue(
    uint Index,
    string Uuid,
    string Name,
    string Unit,
    string Scope,
    string Storage,
    string Result);
