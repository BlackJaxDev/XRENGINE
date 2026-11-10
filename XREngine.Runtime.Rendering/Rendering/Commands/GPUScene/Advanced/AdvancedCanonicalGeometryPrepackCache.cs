using System.Collections;
using XREngine.Data.Rendering;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Packs canonical Advanced geometry on general job workers before the publication needs it.
/// </summary>
/// <remarks>
/// <para>
/// The Advanced publication runs at the frame boundary and used to validate and pack every new
/// mesh on the swap thread. When a large scene activates, that work blocked the swap for more
/// than a second. GPUScene requests a pre-pack when it registers a mesh, which happens during
/// the collect phase, so most entries are ready when the publication runs. The publication then
/// copies a ready entry into its staging arrays and packs only the meshes that missed the cache.
/// </para>
/// <para>
/// One entry exists per mesh and holds one geometry revision. A worker takes the mesh's geometry
/// read scope with a non-blocking enter; when the mesh is busy it tries again on a later job tick.
/// The worker reads the revision before and after the pack and drops the result when it changed.
/// Workers never touch the GPUScene lock or the publication transaction.
/// </para>
/// <para>
/// Entries own plain managed arrays. The publication releases an entry after it appended the
/// geometry to the arenas, so the arrays become garbage and their memory returns to the runtime.
/// </para>
/// </remarks>
internal sealed class AdvancedCanonicalGeometryPrepackCache
{
    private const int StateQueued = 0;
    private const int StateReady = 1;
    private const int StateFailed = 2;
    private const int MaxReadScopeAttempts = 600;

    /// <summary>
    /// Gets whether mesh registration queues worker pre-packs. Set XRE_ADVANCED_GEOMETRY_PREPACK=0
    /// before launch to disable it; the publication then packs every mesh on the swap thread.
    /// </summary>
    internal static bool Enabled { get; } = !string.Equals(
        Environment.GetEnvironmentVariable("XRE_ADVANCED_GEOMETRY_PREPACK"),
        "0",
        StringComparison.Ordinal);

    /// <summary>The packed canonical geometry of one mesh revision.</summary>
    internal sealed class Entry(XRMesh mesh, long revision)
    {
        public XRMesh Mesh { get; } = mesh;
        public long Revision { get; } = revision;
        internal int State;
        public int VertexCount { get; internal set; }
        public int IndexCount { get; internal set; }
        public MeshletPayload? Payload { get; internal set; }
        public bool UseMeshlets { get; internal set; }
        public AdvancedDeformedVertex[] Vertices { get; internal set; } = [];
        public uint[] Indices { get; internal set; } = [];
        public AdvancedMeshletDescriptor[] MeshletDescriptors { get; internal set; } = [];
        public uint[] MeshletVertexIndices { get; internal set; } = [];
        public uint[] MeshletTriangleWords { get; internal set; } = [];
        public bool IsReady => Volatile.Read(ref State) == StateReady;
    }

    private readonly Lock _sync = new();
    private readonly Dictionary<XRMesh, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private long _packedVertexCount;
    private long _packMicroseconds;

    /// <summary>Gets the number of vertices that workers packed since the cache was created.</summary>
    public long PackedVertexCount => Interlocked.Read(ref _packedVertexCount);

    /// <summary>Gets the worker time spent packing, in microseconds.</summary>
    public long PackMicroseconds => Interlocked.Read(ref _packMicroseconds);

    /// <summary>
    /// Queues a pre-pack of the mesh's current geometry revision. A queued or ready entry for the
    /// same revision is kept, so repeated requests cost one dictionary lookup.
    /// </summary>
    public void Request(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Type != EPrimitiveType.Triangles || mesh.VertexCount <= 0)
            return;

        long revision = mesh.GeometryRevision;
        Entry entry;
        lock (_sync)
        {
            if (_entries.TryGetValue(mesh, out Entry? existing) &&
                existing.Revision == revision &&
                Volatile.Read(ref existing.State) != StateFailed)
                return;

            entry = new Entry(mesh, revision);
            _entries[mesh] = entry;
        }

        if (RuntimeRenderingHostServices.Work.GeneralJobs is not { } jobs)
        {
            Volatile.Write(ref entry.State, StateFailed);
            return;
        }

        jobs.Schedule(PackRoutine(entry), priority: JobPriority.High);
    }

    /// <summary>Gets the ready entry for the mesh revision, if a worker finished it.</summary>
    public bool TryGetReady(XRMesh mesh, long revision, out Entry entry)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(mesh, out Entry? found) &&
                found.Revision == revision &&
                found.IsReady)
            {
                entry = found;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>Drops the entry of the mesh. A later request packs it again.</summary>
    public void Release(XRMesh mesh)
    {
        lock (_sync)
            _entries.Remove(mesh);
    }

    /// <summary>Drops every entry.</summary>
    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }

    /// <summary>
    /// Packs the mesh's current revision on the calling thread and stores the entry. The result is
    /// the same entry that a worker produces.
    /// </summary>
    /// <returns>False when the mesh is busy or cannot be packed.</returns>
    internal bool TryPackImmediately(XRMesh mesh, out Entry entry)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        entry = new Entry(mesh, mesh.GeometryRevision);
        lock (_sync)
            _entries[mesh] = entry;
        if (TryPack(entry) != PackResult.Packed)
        {
            Volatile.Write(ref entry.State, StateFailed);
            return false;
        }
        Volatile.Write(ref entry.State, StateReady);
        return true;
    }

    private IEnumerable PackRoutine(Entry entry)
    {
        for (int attempt = 0; attempt < MaxReadScopeAttempts; ++attempt)
        {
            if (!IsCurrent(entry))
                yield break;

            switch (TryPack(entry))
            {
                case PackResult.Packed:
                    Volatile.Write(ref entry.State, StateReady);
                    yield break;
                case PackResult.Invalid:
                    Volatile.Write(ref entry.State, StateFailed);
                    yield break;
            }

            // The mesh is busy (an edit or an upload lease). Try again on a later job tick.
            yield return null;
        }

        Volatile.Write(ref entry.State, StateFailed);
    }

    private bool IsCurrent(Entry entry)
    {
        lock (_sync)
            return _entries.TryGetValue(entry.Mesh, out Entry? current) && ReferenceEquals(current, entry);
    }

    private enum PackResult
    {
        Packed,
        Busy,
        Invalid,
    }

    private PackResult TryPack(Entry entry)
    {
        XRMesh mesh = entry.Mesh;
        XRMesh.BufferCollection buffers = mesh.Buffers;
        if (!buffers.TryAcquireGeometryReadScope(mesh, out XRMesh.BufferCollection.GeometryReadScope scope))
            return mesh.IsDestroyed || mesh.IsDestroyQueued ? PackResult.Invalid : PackResult.Busy;

        using (scope)
        {
            if (mesh.GeometryRevision != entry.Revision)
                return PackResult.Invalid;
            if (!AdvancedGpuScenePublisher.TryValidateCanonicalGeometrySource(mesh) ||
                mesh.Triangles is not { } triangles)
                return PackResult.Invalid;

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int vertexCount = mesh.VertexCount;
            AdvancedDeformedVertex[] vertices = new AdvancedDeformedVertex[vertexCount];
            AdvancedPackedVertexCodec.Pack(mesh, 0u, vertices, 0u);

            uint[] indices = new uint[checked(triangles.Count * 3)];
            int destination = 0;
            for (int triangleIndex = 0; triangleIndex < triangles.Count; ++triangleIndex)
            {
                IndexTriangle triangle = triangles[triangleIndex];
                indices[destination++] = (uint)triangle.Point0;
                indices[destination++] = (uint)triangle.Point1;
                indices[destination++] = (uint)triangle.Point2;
            }

            MeshletPayload? payload = mesh.MeshletPayload;
            bool useMeshlets = payload is { HasMeshlets: true } && payload.IsValidatedFor(mesh);
            if (useMeshlets)
            {
                AdvancedMeshletDescriptor[] descriptors = new AdvancedMeshletDescriptor[payload!.Meshlets.Length];
                AdvancedGpuScenePublisher.ConvertMeshletDescriptors(payload.Meshlets.AsSpan(), descriptors);
                uint[] triangleWords = new uint[checked((int)(((long)payload.TriangleIndices.Length + 3L) / 4L))];
                AdvancedGpuScenePublisher.PackMeshletTriangleWords(payload.TriangleIndices.AsSpan(), triangleWords);
                entry.MeshletDescriptors = descriptors;
                entry.MeshletVertexIndices = payload.VertexIndices.AsSpan().ToArray();
                entry.MeshletTriangleWords = triangleWords;
            }

            if (mesh.GeometryRevision != entry.Revision ||
                !ReferenceEquals(mesh.Buffers, buffers) ||
                !ReferenceEquals(mesh.Triangles, triangles) ||
                !ReferenceEquals(mesh.MeshletPayload, payload) ||
                mesh.VertexCount != vertexCount)
                return PackResult.Invalid;

            entry.VertexCount = vertexCount;
            entry.IndexCount = indices.Length;
            entry.Vertices = vertices;
            entry.Indices = indices;
            entry.Payload = payload;
            entry.UseMeshlets = useMeshlets;
            Interlocked.Add(ref _packedVertexCount, vertexCount);
            Interlocked.Add(ref _packMicroseconds,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            return PackResult.Packed;
        }
    }
}
