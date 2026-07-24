using System.Numerics;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Reusable storage for one slot in the physics debug frame ring.
/// </summary>
internal sealed class PhysicsDebugFrameStorage
{
    private const int MinimumGrowthCapacity = 256;

    public PhysicsDebugPoint[] Points = [];
    public PhysicsDebugLine[] Lines = [];
    public PhysicsDebugTriangle[] Triangles = [];
    public int PointCount;
    public int LineCount;
    public int TriangleCount;
    public int SourcePointCount;
    public int SourceLineCount;
    public int SourceTriangleCount;
    public int DroppedPointCount;
    public int DroppedLineCount;
    public int DroppedTriangleCount;
    public int PublishedByteCount;
    public long Generation;
    public PhysicsDebugSource Source;
    public PhysicsDebugDepthMode DepthMode;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public bool HasBounds;
    public long ExtractionStartTicks;
    public long ExtractionTicks;
    public PhysicsDebugFrameTelemetry Telemetry;
    public int ReaderCount;

    public void Begin(
        PhysicsDebugSource source,
        PhysicsDebugDepthMode depthMode,
        int sourcePointCount,
        int sourceLineCount,
        int sourceTriangleCount,
        long extractionStartTicks)
    {
        PointCount = 0;
        LineCount = 0;
        TriangleCount = 0;
        SourcePointCount = Math.Max(0, sourcePointCount);
        SourceLineCount = Math.Max(0, sourceLineCount);
        SourceTriangleCount = Math.Max(0, sourceTriangleCount);
        DroppedPointCount = 0;
        DroppedLineCount = 0;
        DroppedTriangleCount = 0;
        PublishedByteCount = 0;
        Source = source;
        DepthMode = depthMode;
        HasBounds = false;
        BoundsMin = new Vector3(float.PositiveInfinity);
        BoundsMax = new Vector3(float.NegativeInfinity);
        ExtractionStartTicks = extractionStartTicks;
        ExtractionTicks = 0;
        Telemetry = default;
    }

    public bool AddPoint(in PhysicsDebugPoint point, PhysicsDebugBudget budget)
    {
        const int byteSize = 16;
        if (PointCount >= budget.MaxPoints || PublishedByteCount > budget.MaxBytes - byteSize)
        {
            DroppedPointCount++;
            return false;
        }

        EnsurePointCapacity(PointCount + 1, budget.MaxPoints);
        Points[PointCount++] = point;
        PublishedByteCount += byteSize;
        Include(point.Position);
        return true;
    }

    public bool AddLine(in PhysicsDebugLine line, PhysicsDebugBudget budget)
    {
        const int byteSize = 28;
        if (LineCount >= budget.MaxLines || PublishedByteCount > budget.MaxBytes - byteSize)
        {
            DroppedLineCount++;
            return false;
        }

        EnsureLineCapacity(LineCount + 1, budget.MaxLines);
        Lines[LineCount++] = line;
        PublishedByteCount += byteSize;
        Include(line.Start);
        Include(line.End);
        return true;
    }

    public bool AddTriangle(in PhysicsDebugTriangle triangle, PhysicsDebugBudget budget)
    {
        const int byteSize = 40;
        if (TriangleCount >= budget.MaxTriangles || PublishedByteCount > budget.MaxBytes - byteSize)
        {
            DroppedTriangleCount++;
            return false;
        }

        EnsureTriangleCapacity(TriangleCount + 1, budget.MaxTriangles);
        Triangles[TriangleCount++] = triangle;
        PublishedByteCount += byteSize;
        Include(triangle.A);
        Include(triangle.B);
        Include(triangle.C);
        return true;
    }

    private void EnsurePointCapacity(int required, int budget)
    {
        if (Points.Length >= required)
            return;

        Array.Resize(ref Points, GetGrowthCapacity(Points.Length, required, budget));
    }

    private void EnsureLineCapacity(int required, int budget)
    {
        if (Lines.Length >= required)
            return;

        Array.Resize(ref Lines, GetGrowthCapacity(Lines.Length, required, budget));
    }

    private void EnsureTriangleCapacity(int required, int budget)
    {
        if (Triangles.Length >= required)
            return;

        Array.Resize(ref Triangles, GetGrowthCapacity(Triangles.Length, required, budget));
    }

    private static int GetGrowthCapacity(int current, int required, int budget)
    {
        int grown = current == 0 ? MinimumGrowthCapacity : current;
        while (grown < required && grown < budget)
            grown = checked(grown + Math.Max(grown >> 1, 1));
        return Math.Min(Math.Max(grown, required), budget);
    }

    private void Include(Vector3 point)
    {
        BoundsMin = Vector3.Min(BoundsMin, point);
        BoundsMax = Vector3.Max(BoundsMax, point);
        HasBounds = true;
    }
}
