using System.Numerics;
using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Invalidates derived geometry data after callers mutate exposed vertex/index collections
    /// or write raw buffer memory without completing an <see cref="XRDataBuffer"/> write.
    /// Committed <see cref="XRDataBuffer"/> writes advance this automatically; raw pointer writes
    /// remain intentionally explicit because the buffer cannot observe them.
    /// </summary>
    public void MarkGeometryChanged()
    {
        AdvanceGeometryRevision();
        ClearAccelerationCaches();
        DataChanged?.Invoke(this);
    }

    private void AdvanceGeometryRevision()
    {
        long revision = Interlocked.Increment(ref _geometryRevision);
        if (revision != 0)
            return;

        // Avoid the reserved zero value used to mean "not owner-validated".
        Interlocked.CompareExchange(ref _geometryRevision, 1, 0);
    }

    public void RebuildBoundsFromPositions()
    {
        if (VertexCount <= 0)
        {
            _bounds = new AABB(Vector3.Zero, Vector3.Zero);
            return;
        }

        Vector3 min = GetPosition(0);
        Vector3 max = min;

        for (uint i = 1; i < (uint)VertexCount; i++)
        {
            Vector3 position = GetPosition(i);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        _bounds = new AABB(min, max);
    }

    public void ClearAccelerationCaches()
    {
        TriangleLookup = null;
        _bvhTree = null;
        SignedDistanceField = null;
        Interlocked.Exchange(ref _generatingBvh, 0);
    }

    public void NotifyMeshDataChanged()
        => MarkGeometryChanged();

    internal bool HasCachedIndexBuffer(EPrimitiveType type)
        => _indexBufferCache.ContainsKey(type);

    internal bool HasAccelerationCache()
        => TriangleLookup is not null || _bvhTree is not null || SignedDistanceField is not null;
}
