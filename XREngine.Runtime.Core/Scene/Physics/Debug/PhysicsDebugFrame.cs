using System.Numerics;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Read-only view of one published physics debug visualization generation.
/// The view remains valid for the lifetime of its owning
/// <see cref="PhysicsDebugFrameLease"/>.
/// </summary>
public readonly struct PhysicsDebugFrame
{
    private readonly PhysicsDebugPoint[]? _points;
    private readonly PhysicsDebugLine[]? _lines;
    private readonly PhysicsDebugTriangle[]? _triangles;

    internal PhysicsDebugFrame(PhysicsDebugFrameStorage storage)
    {
        _points = storage.Points;
        _lines = storage.Lines;
        _triangles = storage.Triangles;
        PointCount = storage.PointCount;
        LineCount = storage.LineCount;
        TriangleCount = storage.TriangleCount;
        Generation = storage.Generation;
        Source = storage.Source;
        DepthMode = storage.DepthMode;
        BoundsMin = storage.BoundsMin;
        BoundsMax = storage.BoundsMax;
        HasBounds = storage.HasBounds;
        Telemetry = storage.Telemetry;
    }

    public long Generation { get; }
    public PhysicsDebugSource Source { get; }
    public PhysicsDebugDepthMode DepthMode { get; }
    public int PointCount { get; }
    public int LineCount { get; }
    public int TriangleCount { get; }
    public bool HasBounds { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public PhysicsDebugFrameTelemetry Telemetry { get; }

    public ReadOnlySpan<PhysicsDebugPoint> Points
        => _points.AsSpan(0, PointCount);

    public ReadOnlySpan<PhysicsDebugLine> Lines
        => _lines.AsSpan(0, LineCount);

    public ReadOnlySpan<PhysicsDebugTriangle> Triangles
        => _triangles.AsSpan(0, TriangleCount);

    public bool IsEmpty => PointCount == 0 && LineCount == 0 && TriangleCount == 0;
}
