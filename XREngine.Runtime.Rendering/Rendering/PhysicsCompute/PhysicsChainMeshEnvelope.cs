using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Components;

namespace XREngine.Rendering.Compute;

/// <summary>Computes a cached mesh envelope about its influencing bones.</summary>
public static class PhysicsChainMeshEnvelope
{
    private static readonly ConditionalWeakTable<XRMesh, EnvelopeCache> MeshCaches = new();
    private static readonly ConditionalWeakTable<XRMeshRenderer, EnvelopeCache> RendererCaches = new();

    /// <summary>Gets the largest bind-pose vertex distance from an influencing bone.</summary>
    public static bool TryGetVertexInfluenceRadius(XRMesh mesh, out float radius)
        => TryGetRadius(mesh, null, MeshCaches.GetValue(mesh, static _ => new()), out radius);

    /// <summary>Gets the envelope with the renderer's current authored blendshape weights.</summary>
    public static bool TryGetVertexInfluenceRadius(XRMeshRenderer renderer, out float radius)
    {
        radius = 0.0f;
        if (renderer.Mesh is not { } mesh)
            return false;
        if (mesh.BlendshapeCount == 0u)
            return TryGetVertexInfluenceRadius(mesh, out radius);
        return TryGetRadius(mesh, renderer, RendererCaches.GetValue(renderer, static _ => new()), out radius);
    }

    /// <summary>Gets the local vertex box that the current palette transforms.</summary>
    public static bool TryGetPaletteInputBounds(XRMeshRenderer renderer, out AABB bounds)
        => TryGetPaletteInputBounds(renderer, out bounds, out _);

    /// <summary>Captures the bound and its authored input identity together.</summary>
    public static bool TryGetPaletteInputBounds(XRMeshRenderer renderer, out AABB bounds,
        out PhysicsChainMeshEnvelopeStamp stamp)
    {
        bounds = default;
        stamp = default;
        if (renderer.Mesh is not { } mesh)
            return false;
        EnvelopeCache cache = mesh.BlendshapeCount == 0u
            ? MeshCaches.GetValue(mesh, static _ => new())
            : RendererCaches.GetValue(renderer, static _ => new());
        lock (cache)
        {
            if (!TryGetRadius(mesh, mesh.BlendshapeCount == 0u ? null : renderer, cache, out _))
                return false;
            bounds = cache.LocalBounds;
            stamp = new(mesh, cache.GeometryRevision, cache.WeightsVersion,
                cache.BindRoot, cache.BoneLayout!) { VertexInfluenceRadius = cache.Radius };
            return stamp.Matches(renderer, mesh);
        }
    }

    /// <summary>Bounds the exact blendshape inputs retained by the producer.</summary>
    public static bool TryGetPaletteInputBounds(XRMeshRenderer renderer,
        ReadOnlySpan<PhysicsChainMorphWeight> weights, ulong weightsVersion,
        out AABB bounds, out PhysicsChainMeshEnvelopeStamp stamp)
    {
        bounds = default;
        stamp = default;
        if (renderer.Mesh is not { } mesh)
            return false;
        EnvelopeCache cache = mesh.BlendshapeCount == 0u
            ? MeshCaches.GetValue(mesh, static _ => new())
            : RendererCaches.GetValue(renderer, static _ => new());
        lock (cache)
        {
            if (!cache.SnapshotWeights || !ReferenceEquals(cache.Mesh, mesh) ||
                cache.GeometryRevision != mesh.GeometryRevision ||
                !ReferenceEquals(cache.BoneLayout, mesh.UtilizedBones) ||
                cache.BindRoot != mesh.BindRootMatrix || cache.WeightsVersion != weightsVersion ||
                !weights.SequenceEqual(cache.MorphWeights.AsSpan(0, cache.MorphWeightCount)))
            {
                cache.Mesh = mesh;
                cache.GeometryRevision = mesh.GeometryRevision;
                cache.BoneLayout = mesh.UtilizedBones;
                cache.BindRoot = mesh.BindRootMatrix;
                cache.WeightsVersion = weightsVersion;
                cache.SnapshotWeights = true;
                if (weights.Length > cache.MorphWeights.Length)
                    Array.Resize(ref cache.MorphWeights, weights.Length);
                weights.CopyTo(cache.MorphWeights);
                cache.MorphWeightCount = weights.Length;
                cache.Valid = TryCalculateRadius(mesh, null, out cache.Radius,
                    out cache.LocalBounds, weights, snapshotWeights: true);
            }
            if (!cache.Valid)
                return false;
            bounds = cache.LocalBounds;
            stamp = new(mesh, cache.GeometryRevision, cache.WeightsVersion,
                cache.BindRoot, cache.BoneLayout!) { VertexInfluenceRadius = cache.Radius };
            return stamp.Matches(renderer, mesh);
        }
    }

    /// <summary>Bounds the linear stretch of an affine matrix, including shear.</summary>
    public static float MaximumLinearStretch(in Matrix4x4 matrix)
        => PhysicsChainGpuSpatialInput.MaximumLinearStretch(in matrix);

    private static bool TryGetRadius(XRMesh mesh, XRMeshRenderer? renderer, EnvelopeCache cache, out float radius)
    {
        ulong weightsVersion = renderer?.BlendshapeWeightsVersion ?? 0UL;
        lock (cache)
        {
            if (cache.SnapshotWeights || !ReferenceEquals(cache.Mesh, mesh) || cache.GeometryRevision != mesh.GeometryRevision ||
                !ReferenceEquals(cache.BoneLayout, mesh.UtilizedBones) || cache.BindRoot != mesh.BindRootMatrix ||
                cache.WeightsVersion != weightsVersion)
            {
                cache.Mesh = mesh;
                cache.GeometryRevision = mesh.GeometryRevision;
                cache.BoneLayout = mesh.UtilizedBones;
                cache.BindRoot = mesh.BindRootMatrix;
                cache.WeightsVersion = weightsVersion;
                cache.SnapshotWeights = false;
                cache.Valid = TryCalculateRadius(mesh, renderer, out cache.Radius, out cache.LocalBounds);
            }
            radius = cache.Radius;
            return cache.Valid;
        }
    }

    private static bool TryCalculateRadius(XRMesh mesh, XRMeshRenderer? renderer, out float radius, out AABB bounds,
        ReadOnlySpan<PhysicsChainMorphWeight> retainedWeights = default, bool snapshotWeights = false)
    {
        radius = 0.0f;
        bounds = default;
        if (!XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader influences) ||
            (mesh.Interleaved ? mesh.InterleavedVertexBuffer : mesh.PositionsBuffer)?.ClientSideSource is null)
            return false;

        XRMeshBlendshapeActiveListReader blendshapes = default;
        XRDataBuffer? weights = renderer?.CaptureBlendshapeResources().Weights;
        bool hasBlendshapes = (renderer is not null || snapshotWeights) && mesh.BlendshapeCount != 0u;
        if (hasBlendshapes && ((!snapshotWeights && (weights is null || weights.ElementCount < mesh.BlendshapeCount)) ||
            !XRMeshBlendshapeActiveListReader.TryCreate(mesh, out blendshapes)))
            return false;

        foreach (PhysicsChainMorphWeight retained in retainedWeights)
            if (retained.ShapeIndex >= mesh.BlendshapeCount || !float.IsFinite(retained.Weight))
                return false;

        int capacity = influences.MaxInfluenceCount;
        int[]? pooledIndices = null;
        float[]? pooledWeights = null;
        Span<int> boneIndices = capacity <= 64 ? stackalloc int[capacity] : (pooledIndices = ArrayPool<int>.Shared.Rent(capacity)).AsSpan(0, capacity);
        Span<float> boneWeights = capacity <= 64 ? stackalloc float[capacity] : (pooledWeights = ArrayPool<float>.Shared.Rent(capacity)).AsSpan(0, capacity);
        Matrix4x4 bindRoot = mesh.BindRootMatrix ?? Matrix4x4.Identity;
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        try
        {
            for (int vertex = 0; vertex < mesh.VertexCount; ++vertex)
            {
                Vector3 position = mesh.GetPosition(checked((uint)vertex));
                if (!IsFinite(position))
                    return false;
                Vector3 morphExtent = Vector3.Zero;
                if (hasBlendshapes)
                {
                    blendshapes.GetVertexEntries(vertex, out int first, out int count);
                    for (int entry = first; entry < first + count; ++entry)
                    {
                        blendshapes.ReadEntry(entry, out int shape, out Vector3 delta, out _, out _);
                        if ((uint)shape >= mesh.BlendshapeCount || !IsFinite(delta))
                            return false;
                        float weight = 0.0f;
                        if (snapshotWeights)
                        {
                            // Duplicate entries also contribute to aggregate deformation.
                            foreach (PhysicsChainMorphWeight retained in retainedWeights)
                                if (retained.ShapeIndex == (uint)shape)
                                    weight += MathF.Abs(retained.Weight);
                        }
                        else
                            weight = weights!.GetFloat((uint)shape);
                        if (!float.IsFinite(weight))
                            return false;
                        morphExtent += Vector3.Abs(delta) * MathF.Abs(weight);
                    }
                }

                minimum = Vector3.Min(minimum, position - morphExtent);
                maximum = Vector3.Max(maximum, position + morphExtent);

                int countInfluences = influences.ReadInfluences(vertex, boneIndices, boneWeights);
                // An identity-skinned vertex has no moving chain anchor.
                if (countInfluences == 0)
                    return false;
                for (int influence = 0; influence < countInfluences; ++influence)
                {
                    Matrix4x4 bind = bindRoot * mesh.UtilizedBones[boneIndices[influence]].invBindWorldMtx;
                    if (!IsFiniteAffineMatrix(in bind))
                        return false;
                    Vector3 boneLocalPosition = Vector3.Transform(position, bind);
                    Vector3 boneLocalMorphExtent = TransformExtents(morphExtent, bind);
                    float distance = boneLocalPosition.Length() + boneLocalMorphExtent.Length();
                    if (!float.IsFinite(distance))
                        return false;
                    radius = MathF.Max(radius, distance);
                }
            }
            // Positive normalized weights form a convex combination. The largest
            // influencing-bone radius bounds that combination after bone motion.
            radius = MathF.BitIncrement(radius + 0.0001f * MathF.Max(1.0f, radius));
            Vector3 padding = Vector3.Max(Vector3.Abs(minimum), Vector3.Abs(maximum)) * 0.0001f
                + new Vector3(0.0001f);
            bounds = new AABB(minimum - padding, maximum + padding);
            return float.IsFinite(radius) && bounds.IsValid && IsFinite(bounds.Min) && IsFinite(bounds.Max);
        }
        finally
        {
            if (pooledIndices is not null)
                ArrayPool<int>.Shared.Return(pooledIndices);
            if (pooledWeights is not null)
                ArrayPool<float>.Shared.Return(pooledWeights);
        }
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFiniteAffineMatrix(in Matrix4x4 matrix)
        => IsFinite(new Vector3(matrix.M11, matrix.M12, matrix.M13)) &&
            IsFinite(new Vector3(matrix.M21, matrix.M22, matrix.M23)) &&
            IsFinite(new Vector3(matrix.M31, matrix.M32, matrix.M33)) &&
            IsFinite(matrix.Translation) && matrix.M14 == 0.0f &&
            matrix.M24 == 0.0f && matrix.M34 == 0.0f && matrix.M44 == 1.0f;

    private static Vector3 TransformExtents(in Vector3 extents, in Matrix4x4 matrix)
        => new(
            MathF.Abs(matrix.M11) * extents.X + MathF.Abs(matrix.M21) * extents.Y + MathF.Abs(matrix.M31) * extents.Z,
            MathF.Abs(matrix.M12) * extents.X + MathF.Abs(matrix.M22) * extents.Y + MathF.Abs(matrix.M32) * extents.Z,
            MathF.Abs(matrix.M13) * extents.X + MathF.Abs(matrix.M23) * extents.Y + MathF.Abs(matrix.M33) * extents.Z);

    private sealed class EnvelopeCache
    {
        public XRMesh? Mesh;
        public object? BoneLayout;
        public Matrix4x4? BindRoot;
        public long GeometryRevision = -1;
        public ulong WeightsVersion;
        public float Radius;
        public AABB LocalBounds;
        public bool Valid;
        public bool SnapshotWeights;
        public PhysicsChainMorphWeight[] MorphWeights = [];
        public int MorphWeightCount;
    }
}
