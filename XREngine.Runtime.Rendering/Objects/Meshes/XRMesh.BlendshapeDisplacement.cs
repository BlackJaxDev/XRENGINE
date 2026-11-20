using System.Numerics;
using System.Threading;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>The computed displacement and the delta buffer and geometry revision it was read from.</summary>
    private sealed record BlendshapeDisplacementCache(XRDataBuffer? Source, long Revision, float Value);

    private BlendshapeDisplacementCache? _blendshapeDisplacementCache;

    /// <summary>
    /// Returns the largest distance that the blendshapes can move one vertex, in mesh units: for
    /// each vertex, the sum of the lengths of its position deltas over every shape, then the largest
    /// sum. With shape weights in [0, 1] no vertex moves farther. The Advanced cluster cut of a
    /// skinned draw subtracts it from the camera distance, because the posed draw bounds come from
    /// the bones and do not contain the blendshape deltas.
    /// </summary>
    /// <remarks>
    /// Returns 0 for a mesh without blendshapes and <see cref="float.MaxValue"/> when the mesh has
    /// blendshapes whose buffers have no CPU copy, so a caller treats an unknown displacement as
    /// unbounded. The first call reads every blendshape entry once; later calls return the cached
    /// value until the delta buffer or the geometry revision changes.
    /// </remarks>
    public float GetMaxBlendshapeDisplacement()
    {
        XRDataBuffer? source = BlendshapeDeltas;
        long revision = GeometryRevision;
        BlendshapeDisplacementCache? cache = Volatile.Read(ref _blendshapeDisplacementCache);
        if (cache is not null && ReferenceEquals(cache.Source, source) && cache.Revision == revision)
            return cache.Value;

        float value = CalculateMaxBlendshapeDisplacement();
        Volatile.Write(ref _blendshapeDisplacementCache, new BlendshapeDisplacementCache(source, revision, value));
        return value;
    }

    private float CalculateMaxBlendshapeDisplacement()
    {
        if (!HasBlendshapes)
            return 0.0f;
        if (!XRMeshBlendshapeActiveListReader.TryCreate(this, out XRMeshBlendshapeActiveListReader reader))
            return float.MaxValue;

        float largest = 0.0f;
        for (int vertexIndex = 0; vertexIndex < reader.VertexCount; vertexIndex++)
        {
            reader.GetVertexEntries(vertexIndex, out int firstEntry, out int count);
            float sum = 0.0f;
            for (int entry = firstEntry; entry < firstEntry + count; entry++)
            {
                reader.ReadEntry(entry, out _, out Vector3 positionDelta, out _, out _);
                sum += positionDelta.Length();
            }
            largest = MathF.Max(largest, sum);
        }

        return float.IsFinite(largest) ? largest : float.MaxValue;
    }
}
