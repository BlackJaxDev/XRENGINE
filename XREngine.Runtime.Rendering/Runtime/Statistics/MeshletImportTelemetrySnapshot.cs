namespace XREngine;

/// <summary>
/// Allocation-free snapshot of monotonic meshlet import/cache evidence.
/// Callers that need a scoped measurement subtract a baseline snapshot rather
/// than supplying inferred parser or builder counts to telemetry.
/// </summary>
public readonly record struct MeshletImportTelemetrySnapshot(
    long SourceParserCalls,
    long NativeBuilderCalls,
    long WarmPayloadHydrations)
{
    /// <summary>Returns saturated non-negative counter deltas from <paramref name="baseline"/>.</summary>
    public MeshletImportTelemetrySnapshot DeltaFrom(MeshletImportTelemetrySnapshot baseline)
        => new(
            Math.Max(0L, SourceParserCalls - baseline.SourceParserCalls),
            Math.Max(0L, NativeBuilderCalls - baseline.NativeBuilderCalls),
            Math.Max(0L, WarmPayloadHydrations - baseline.WarmPayloadHydrations));
}
