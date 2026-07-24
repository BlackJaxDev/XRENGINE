using System.Diagnostics;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Reusable writer for one physics debug frame slot. Publishers create and cache
/// these writers; beginning and publishing a frame do not allocate.
/// </summary>
public sealed class PhysicsDebugFrameWriter
{
    private PhysicsDebugFramePublisher? _publisher;
    private PhysicsDebugFrameStorage? _storage;
    private PhysicsDebugBudget _budget;

    internal PhysicsDebugFrameWriter()
    {
    }

    internal void Activate(
        PhysicsDebugFramePublisher publisher,
        PhysicsDebugFrameStorage storage,
        PhysicsDebugBudget budget)
    {
        _publisher = publisher;
        _storage = storage;
        _budget = budget;
    }

    public bool IsValid => _publisher is not null && _storage is not null;
    public int PointCount => _storage?.PointCount ?? 0;
    public int LineCount => _storage?.LineCount ?? 0;
    public int TriangleCount => _storage?.TriangleCount ?? 0;

    public bool AddPoint(in PhysicsDebugPoint point)
        => _storage?.AddPoint(point, _budget) ?? false;

    public bool AddLine(in PhysicsDebugLine line)
        => _storage?.AddLine(line, _budget) ?? false;

    public bool AddTriangle(in PhysicsDebugTriangle triangle)
        => _storage?.AddTriangle(triangle, _budget) ?? false;

    public void CompleteSourceCountsFromPublished()
    {
        if (_storage is null)
            return;

        _storage.SourcePointCount = _storage.PointCount + _storage.DroppedPointCount;
        _storage.SourceLineCount = _storage.LineCount + _storage.DroppedLineCount;
        _storage.SourceTriangleCount = _storage.TriangleCount + _storage.DroppedTriangleCount;
    }

    public void Publish()
    {
        if (_publisher is null || _storage is null)
            return;

        _storage.ExtractionTicks = Stopwatch.GetTimestamp() - _storage.ExtractionStartTicks;
        _publisher.Publish(_storage);
        _publisher = null;
        _storage = null;
    }
}
