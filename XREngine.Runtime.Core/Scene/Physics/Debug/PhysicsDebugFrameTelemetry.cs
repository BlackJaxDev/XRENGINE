namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Allocation-free counters and timings for one published physics debug frame.
/// </summary>
public readonly record struct PhysicsDebugFrameTelemetry(
    long Generation,
    PhysicsDebugSource Source,
    int SourcePointCount,
    int SourceLineCount,
    int SourceTriangleCount,
    int PublishedPointCount,
    int PublishedLineCount,
    int PublishedTriangleCount,
    int DroppedPointCount,
    int DroppedLineCount,
    int DroppedTriangleCount,
    int PublishedByteCount,
    long ExtractionTicks,
    long PublicationTicks)
{
    public int PublishedPrimitiveCount
        => PublishedPointCount + PublishedLineCount + PublishedTriangleCount;

    public int DroppedPrimitiveCount
        => DroppedPointCount + DroppedLineCount + DroppedTriangleCount;
}
