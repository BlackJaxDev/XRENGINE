using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Requests the triangle, line and point index streams before draw admission.
    /// Repeated requests share the mesh's existing per-primitive tickets and cache.
    /// This never joins a worker and never creates a backend wrapper or uploads data.
    /// </summary>
    /// <remarks>
    /// Call after CPU topology construction is complete, on its owning preparation
    /// thread. Snapshot capture and edits to the exposed lists must be serialized.
    /// A cache miss copies the topology once; conversion uses only that owned copy.
    /// Readiness/failure are still reported by GetIndexBuffer and the existing build
    /// failure query. A request alone does not establish draw readiness.
    /// </remarks>
    public void RequestIndexBufferPreparation()
    {
        _ = GetIndexBuffer(EPrimitiveType.Triangles, out _);
        _ = GetIndexBuffer(EPrimitiveType.Lines, out _);
        _ = GetIndexBuffer(EPrimitiveType.Points, out _);
    }

    internal bool AreIndexBufferBindingsCurrent(
        XRDataBuffer? triangles,
        XRDataBuffer? lines,
        XRDataBuffer? points)
    {
        lock (_indexBufferLock)
            return IndexBufferBindingMatchesNoLock(EPrimitiveType.Triangles, triangles) &&
                IndexBufferBindingMatchesNoLock(EPrimitiveType.Lines, lines) &&
                IndexBufferBindingMatchesNoLock(EPrimitiveType.Points, points);
    }

    private bool IndexBufferBindingMatchesNoLock(EPrimitiveType type, XRDataBuffer? buffer)
        => HasIndexData(type)
            ? _indexBufferCache.TryGetValue(type, out var cached) && ReferenceEquals(cached.buffer, buffer)
            : buffer is null;

    // Capture scalars, not primitive-object references: even when a primitive's
    // representation changes, the worker must not observe subsequent authoring edits.
    // Avoid GetIndices' per-triangle/per-line temporary arrays on this cold path.
    private int[] CaptureIndexBufferIndices(EPrimitiveType type)
    {
        if (type == EPrimitiveType.Patches)
            type = PatchVertices switch
            {
                1 => EPrimitiveType.Points,
                2 => EPrimitiveType.Lines,
                3 => EPrimitiveType.Triangles,
                _ => EPrimitiveType.Patches,
            };

        switch (type)
        {
            case EPrimitiveType.Triangles when _triangles is { Count: > 0 } triangles:
            {
                int[] indices = new int[checked(triangles.Count * 3)];
                int offset = 0;
                foreach (IndexTriangle triangle in triangles)
                {
                    indices[offset++] = triangle.Point0;
                    indices[offset++] = triangle.Point1;
                    indices[offset++] = triangle.Point2;
                }
                return indices;
            }
            case EPrimitiveType.Lines when _lines is { Count: > 0 } lines:
            {
                int[] indices = new int[checked(lines.Count * 2)];
                int offset = 0;
                foreach (IndexLine line in lines)
                {
                    indices[offset++] = line.Point0;
                    indices[offset++] = line.Point1;
                }
                return indices;
            }
            case EPrimitiveType.Points when _points is { Count: > 0 } points:
                return points.ToArray();
            default:
                return [];
        }
    }
}
