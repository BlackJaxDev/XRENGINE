using System.Numerics;
using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
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
        => DataChanged?.Invoke(this);

    internal bool HasCachedIndexBuffer(EPrimitiveType type)
        => _indexBufferCache.ContainsKey(type);

    internal bool HasAccelerationCache()
        => TriangleLookup is not null || _bvhTree is not null || SignedDistanceField is not null;
}
