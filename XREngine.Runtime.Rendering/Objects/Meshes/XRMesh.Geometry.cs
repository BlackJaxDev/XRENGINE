using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using SimpleScene.Util.ssBVH;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMesh
{
    // Cached index buffers keyed by primitive type.
    // Only caches buffers created for the default ElementArrayBuffer target.
    // Prevents the render-resource registry from destroying a still-in-use
    // VkBuffer when multiple VkMeshRenderers share the same mesh.
    private readonly object _indexBufferLock = new();
    private readonly Dictionary<EPrimitiveType, (XRDataBuffer buffer, IndexSize elementSize)> _indexBufferCache = new();
    private readonly Dictionary<EPrimitiveType, IndexBufferBuildTicket> _indexBufferBuildTickets = new();

    /// <summary>
    /// Clears the cached index buffers so the next call to
    /// <see cref="GetIndexBuffer"/> will recreate them.
    /// Pass <c>null</c> to clear all, or a specific type to clear just that.
    /// </summary>
    public void InvalidateIndexBufferCache(EPrimitiveType? type = null)
    {
        HashSet<XRDataBuffer> removedBuffers = new(ReferenceEqualityComparer.Instance);
        lock (_indexBufferLock)
        {
            if (type.HasValue)
            {
                if (_indexBufferCache.Remove(type.Value, out var removed))
                    removedBuffers.Add(removed.buffer);
                InvalidateBuildTicketNoLock(type.Value);
            }
            else
            {
                foreach (var cached in _indexBufferCache.Values)
                    removedBuffers.Add(cached.buffer);
                _indexBufferCache.Clear();
                foreach (IndexBufferBuildTicket ticket in _indexBufferBuildTickets.Values)
                    ticket.Fail(new IndexBufferBuildInvalidatedException());
                _indexBufferBuildTickets.Clear();
            }
        }

        foreach (XRDataBuffer buffer in removedBuffers)
            DisposeIndexBuffer(buffer);
    }

    /// <summary>
    /// Allocates a flattened copy of the current topology's indices.
    /// Use <see cref="IndexCount"/> or <see cref="HasIndexData"/> for hot-path metadata queries.
    /// </summary>
    public int[]? GetIndices()
        => GetIndices(Type);

    /// <summary>
    /// Allocates a flattened copy of the requested topology's indices. Count and
    /// presence checks must read the primitive lists instead of materializing this array.
    /// </summary>
    public int[]? GetIndices(EPrimitiveType type) => type switch
    {
        EPrimitiveType.Triangles => _triangles?.SelectMany(x => new[] { x.Point0, x.Point1, x.Point2 }).ToArray(),
        EPrimitiveType.Lines => _lines?.SelectMany(x => new[] { x.Point0, x.Point1 }).ToArray(),
        EPrimitiveType.Points => _points?.Select(x => (int)x).ToArray(),
        // Patch draws reuse the existing primitive lists as control-point streams.
        EPrimitiveType.Patches => PatchVertices switch
        {
            1 => _points?.Select(x => (int)x).ToArray(),
            2 => _lines?.SelectMany(x => new[] { x.Point0, x.Point1 }).ToArray(),
            3 => _triangles?.SelectMany(x => new[] { x.Point0, x.Point1, x.Point2 }).ToArray(),
            _ => null,
        },
        _ => null
    };

    private Remapper? SetTriangleIndices(Vertex[] vertices, bool remap = true)
    {
        InvalidateIndexBufferCache(EPrimitiveType.Triangles);
        _triangles = new List<IndexTriangle>(vertices.Length / 3);
        if (remap)
        {
            Remapper remapper = new();
            remapper.Remap(vertices, null);
            for (int i = 0; i < remapper.RemapTable?.Length;)
                _triangles.Add(new IndexTriangle(remapper.RemapTable[i++], remapper.RemapTable[i++], remapper.RemapTable[i++]));
            AdvanceGeometryRevision();
            return remapper;
        }
        for (int i = 0; i < vertices.Length;)
            _triangles.Add(new IndexTriangle(i++, i++, i++));
        AdvanceGeometryRevision();
        return null;
    }

    private Remapper? SetLineIndices(Vertex[] vertices, bool remap = true)
    {
        InvalidateIndexBufferCache(EPrimitiveType.Lines);
        _lines = new List<IndexLine>(vertices.Length / 2);
        if (remap)
        {
            Remapper remapper = new();
            remapper.Remap(vertices, null);
            for (int i = 0; i < remapper.RemapTable?.Length;)
                _lines.Add(new IndexLine(remapper.RemapTable[i++], remapper.RemapTable[i++]));
            AdvanceGeometryRevision();
            return remapper;
        }
        for (int i = 0; i < vertices.Length;)
            _lines.Add(new IndexLine(i++, i++));
        AdvanceGeometryRevision();
        return null;
    }

    private Remapper? SetPointIndices(Vertex[] vertices, bool remap = true)
    {
        InvalidateIndexBufferCache(EPrimitiveType.Points);
        _points = new List<int>(vertices.Length);
        if (remap)
        {
            Remapper remapper = new();
            remapper.Remap(vertices, null);
            for (int i = 0; i < remapper.RemapTable?.Length;)
                _points.Add(remapper.RemapTable[i++]);
            AdvanceGeometryRevision();
            return remapper;
        }
        for (int i = 0; i < vertices.Length;)
            _points.Add(i++);
        AdvanceGeometryRevision();
        return null;
    }

    // BVH / Intersection
    public BVH<Triangle>? BVHTree
    {
        get
        {
            if (!AllowBVHGeneration)
                return _bvhTree;

            if (_bvhTree is null && Interlocked.CompareExchange(ref _generatingBvh, 1, 0) == 0)
            {
                try
                {
                    _ = Task.Run(GenerateBVH);
                }
                catch
                {
                    Interlocked.Exchange(ref _generatingBvh, 0);
                    throw;
                }
            }
            return _bvhTree;
        }
    }

    public BVH<Triangle>? CachedBVHTree => _bvhTree;

    private IEnumerable GenerateBVHJob()
    {
        var task = Task.Run(GenerateBVH);
        yield return task;
    }

    private bool _allowBVHGeneration = false;
    public bool AllowBVHGeneration
    {
        get => _allowBVHGeneration;
        set => SetField(ref _allowBVHGeneration, value);
    }

    public void GenerateBVH()
    {
        if (!AllowBVHGeneration)
            return;
        
        try
        {
            if (Triangles is null)
                return;

            List<Triangle> triangles = new(Triangles.Count);
            List<IndexTriangle> indexTriangles = new(Triangles.Count);

            for (int i = 0; i < Triangles.Count; i++)
            {
                IndexTriangle indices = Triangles[i];
                Triangle triangle = GetTriangle(indices);
                triangles.Add(triangle);
                indexTriangles.Add(indices);
            }

            // Try loading a previously cached BVH from disk.
            if (BvhDiskCache.TryLoad(triangles, indexTriangles, out BVH<Triangle>? cachedBvh, out var cachedLookup)
                && cachedBvh is not null && cachedLookup is not null)
            {
                TriangleLookup = cachedLookup;
                _bvhTree = cachedBvh;
                return;
            }

            // Cache miss — build the BVH from scratch.
            Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)> triangleLookup = new(Triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
                triangleLookup[triangles[i]] = (indexTriangles[i], i);

            TriangleLookup = triangleLookup;
            _bvhTree = new(new TriangleAdapter(), triangles);

            // Store the freshly built BVH to disk for next time.
            BvhDiskCache.TryStore(triangles, triangleLookup, _bvhTree);
        }
        catch (Exception ex)
        {
            RuntimeRenderingHostServices.Diagnostics.LogException(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _generatingBvh, 0);
        }
    }

    private Triangle GetTriangle(IndexTriangle idx)
    {
        Vector3 p0 = GetPosition((uint)idx.Point0);
        Vector3 p1 = GetPosition((uint)idx.Point1);
        Vector3 p2 = GetPosition((uint)idx.Point2);
        return new Triangle(p0, p1, p2);
    }

    public float? Intersect(Segment localSegment, out Triangle? triangle)
    {
        triangle = null;
        BVH<Triangle>? bvh = CachedBVHTree;
        if (bvh is null)
            return null;

        Vector3 direction = localSegment.End - localSegment.Start;
        float segmentLength = direction.Length();
        if (segmentLength <= float.Epsilon)
            return null;
        direction /= segmentLength;

        var matches = bvh.Traverse(x => GeoUtil.Intersect.SegmentWithAABB(localSegment.Start, localSegment.End, x.Min, x.Max, out _, out _));

        float? minDist = null;
        foreach (var node in matches)
        {
            if (node.gobjects is null || node.gobjects.Count == 0)
                continue;

            for (int i = 0; i < node.gobjects.Count; i++)
            {
                Triangle tri = node.gobjects[i];
                if (!GeoUtil.Intersect.RayWithTriangle(localSegment.Start, direction, tri.A, tri.B, tri.C, out float dist)
                    || dist < 0.0f
                    || dist > segmentLength
                    || (minDist.HasValue && dist >= minDist.Value))
                    continue;

                minDist = dist;
                triangle = tri;
            }
        }
        return minDist;
    }

    // SDF
    public void GenerateSDF(IVector3 resolution)
    {
        SignedDistanceField = new();
        var shader = ShaderHelper.LoadEngineShader("Compute//sdfgen.comp");
        var program = new XRRenderProgram(true, true, shader);
        XRDataBuffer verticesBuffer = Buffers[ECommonBufferType.Position.ToString()].Clone(false, EBufferTarget.ShaderStorageBuffer);
        verticesBuffer.AttributeName = "Vertices";
        XRDataBuffer indicesBuffer = GetIndexBuffer(EPrimitiveType.Triangles, out _, EBufferTarget.ShaderStorageBuffer)!;
        indicesBuffer.AttributeName = "Indices";
        program.BindImageTexture(0, SignedDistanceField, 0, false, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.RGB8);
        program.Uniform("sdfMinBounds", Bounds.Min);
        program.Uniform("sdfMaxBounds", Bounds.Max);
        program.Uniform("sdfResolution", resolution);
        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(() =>
        {
            const int ls = 8;
            RuntimeRenderingHostServices.BackendInterop.DispatchCompute(
                program,
                (uint)((resolution.X + ls - 1) / ls),
                (uint)((resolution.Y + ls - 1) / ls),
                (uint)((resolution.Z + ls - 1) / ls));
        }, RenderThreadJobKind.RequiresGraphicsContext);
    }

    // Index buffer helpers
    /// <summary>
    /// Returns a CPU-prepared index buffer for the given primitive type.
    ///
    /// For the default <see cref="EBufferTarget.ElementArrayBuffer"/> target, the requesting
    /// topology owner captures an immutable index/vertex-count snapshot before scheduling
    /// conversion and CPU buffer population on a background <see cref="Task.Run"/>. Native
    /// wrapper materialization and upload retain their existing renderer-thread ownership.
    /// The first call after invalidation returns <c>null</c> unless explicitly synchronous;
    /// subsequent calls reuse the cached buffer. Snapshotting still has a cold O(index count)
    /// cost: request preparation before draw admission, not from command recording.
    ///
    /// Non-default targets (e.g. <see cref="EBufferTarget.ShaderStorageBuffer"/> for compute) are
    /// rare and off the render hot path, so they build synchronously and bypass the cache.
    /// </summary>
    /// <param name="onReady">
    /// Optional completion callback. Invoked synchronously if the buffer is already cached, or on
    /// the background task thread when the async build completes. Callers that need the callback
    /// on a specific thread should marshal inside the callback body. This is a readiness
    /// notification, not a resource lease: consumers must resolve current bindings on their
    /// owning thread rather than retaining a callback buffer across topology invalidation.
    /// </param>
    /// <param name="requireSynchronous">
    /// Explicit off-draw callers may wait for the per-primitive build ticket. The wait never
    /// holds the cache lock and propagates terminal failure. Normal draw admission must leave
    /// this false and preserve its pending cohort until the exact geometry is ready.
    /// </param>
    public XRDataBuffer? GetIndexBuffer(
        EPrimitiveType type,
        out IndexSize elementSize,
        EBufferTarget target = EBufferTarget.ElementArrayBuffer,
        Action<XRDataBuffer, IndexSize>? onReady = null,
        bool requireSynchronous = false)
    {
        ObjectDisposedException.ThrowIf(IsDestroyed, this);
        elementSize = IndexSize.TwoBytes;

        // Non-default targets bypass the cache and build synchronously.
        if (target != EBufferTarget.ElementArrayBuffer)
        {
            var syncBuf = BuildIndexBuffer(type, target, out elementSize);
            if (syncBuf is not null)
                onReady?.Invoke(syncBuf, elementSize);
            return syncBuf;
        }

        while (true)
        {
            (XRDataBuffer buffer, IndexSize elementSize)? cached = null;
            IndexBufferBuildTicket? ticket = null;
            lock (_indexBufferLock)
            {
                ObjectDisposedException.ThrowIf(IsDestroyed, this);
                // Recheck on every retry: invalidation may have made the mesh nonindexed.
                if (!HasIndexData(type))
                    return null;

                if (_indexBufferCache.TryGetValue(type, out var cachedResult))
                    cached = cachedResult;
                else
                    ticket = GetOrStartIndexBufferBuildNoLock(type);
            }

            if (cached is { } cachedBuffer)
            {
                elementSize = cachedBuffer.elementSize;
                onReady?.Invoke(cachedBuffer.buffer, cachedBuffer.elementSize);
                return cachedBuffer.buffer;
            }

            if (requireSynchronous)
            {
                try
                {
                    (XRDataBuffer buffer, IndexSize completedElementSize) =
                        ticket!.Completion.Task.GetAwaiter().GetResult();
                    lock (_indexBufferLock)
                    {
                        ObjectDisposedException.ThrowIf(IsDestroyed, this);
                        if (!IsCurrentIndexBufferBuildNoLock(type, ticket!, buffer))
                            continue;
                    }
                    elementSize = completedElementSize;
                    onReady?.Invoke(buffer, completedElementSize);
                    return buffer;
                }
                catch (IndexBufferBuildInvalidatedException)
                {
                    // A topology mutation won the race with the worker. Acquire the
                    // replacement ticket rather than publishing obsolete indices.
                    continue;
                }
            }

            if (onReady is not null)
                RegisterIndexBufferReadyCallback(type, ticket!, onReady);
            return null;
        }
    }

    /// <summary>
    /// Requests mesh-owned primitive streams before program/draw readiness is polled.
    /// Call on the topology owner after edits are committed; no task is queued for
    /// absent streams, unchanged cached buffers, or an already pending/failed ticket.
    /// </summary>
    internal void RequestIndexBufferPreparation()
    {
        _ = GetIndexBuffer(EPrimitiveType.Triangles, out _);
        _ = GetIndexBuffer(EPrimitiveType.Lines, out _);
        _ = GetIndexBuffer(EPrimitiveType.Points, out _);
    }

    internal bool HasIndexData(EPrimitiveType type) => type switch
    {
        EPrimitiveType.Triangles => _triangles is { Count: > 0 },
        EPrimitiveType.Lines => _lines is { Count: > 0 },
        EPrimitiveType.Points => _points is { Count: > 0 },
        EPrimitiveType.Patches => PatchVertices switch
        {
            1 => _points is { Count: > 0 },
            2 => _lines is { Count: > 0 },
            3 => _triangles is { Count: > 0 },
            _ => false,
        },
        _ => false,
    };

    internal bool TryGetIndexBufferBuildFailure(EPrimitiveType type, out Exception? error)
    {
        lock (_indexBufferLock)
        {
            if (_indexBufferBuildTickets.TryGetValue(type, out IndexBufferBuildTicket? ticket) &&
                ticket.Completion.Task.IsFaulted)
            {
                error = ticket.Completion.Task.Exception?.GetBaseException();
                return error is not null;
            }
        }

        error = null;
        return false;
    }

    private IndexBufferBuildTicket GetOrStartIndexBufferBuildNoLock(EPrimitiveType type)
    {
        if (_indexBufferBuildTickets.TryGetValue(type, out IndexBufferBuildTicket? ticket))
            return ticket;

        ticket = new IndexBufferBuildTicket(GeometryRevision);
        _indexBufferBuildTickets.Add(type, ticket);
        try
        {
            // Primitive lists remain owner-mutated. Copy their scalar indices now:
            // workers must not enumerate mutable lists or read a later VertexCount.
            // The snapshot belongs only to this work item, not the retained ticket.
            int vertexCount = VertexCount;
            int[] indices = CaptureIndexBuildSnapshot(type);
            if (ticket.GeometryRevision != GeometryRevision)
            {
                InvalidateBuildTicketNoLock(type);
                return ticket;
            }

            _ = Task.Run(() => BuildIndexBufferWorker(type, ticket, indices, vertexCount));
        }
        catch (Exception ex)
        {
            // Snapshot/scheduling failure is terminal for this ticket, just like a
            // worker failure. Never leave a task that was not queued pending forever.
            ticket.Fail(ex);
        }
        return ticket;
    }

    private int[] CaptureIndexBuildSnapshot(EPrimitiveType type)
    {
        if (type == EPrimitiveType.Patches)
            type = PatchVertices switch
            {
                1 => EPrimitiveType.Points,
                2 => EPrimitiveType.Lines,
                3 => EPrimitiveType.Triangles,
                _ => EPrimitiveType.Patches,
            };

        // One scalar array, without a temporary allocation for every primitive.
        // Callers must serialize edits with snapshot capture and explicitly invalidate
        // after raw list edits, as required by the existing mesh-editing contract.
        switch (type)
        {
            case EPrimitiveType.Triangles when _triangles is { } triangles:
            {
                int count = triangles.Count;
                int[] indices = new int[checked(count * 3)];
                for (int i = 0, offset = 0; i < count; i++)
                {
                    IndexTriangle triangle = triangles[i];
                    indices[offset++] = triangle.Point0;
                    indices[offset++] = triangle.Point1;
                    indices[offset++] = triangle.Point2;
                }
                return indices;
            }
            case EPrimitiveType.Lines when _lines is { } lines:
            {
                int count = lines.Count;
                int[] indices = new int[checked(count * 2)];
                for (int i = 0, offset = 0; i < count; i++)
                {
                    IndexLine line = lines[i];
                    indices[offset++] = line.Point0;
                    indices[offset++] = line.Point1;
                }
                return indices;
            }
            case EPrimitiveType.Points when _points is { } points:
                return points.ToArray();
            default:
                return [];
        }
    }

    private bool IsCurrentIndexBufferBuildNoLock(
        EPrimitiveType type,
        IndexBufferBuildTicket ticket,
        XRDataBuffer buffer)
        => _indexBufferBuildTickets.TryGetValue(type, out IndexBufferBuildTicket? current) &&
           ReferenceEquals(current, ticket) &&
           _indexBufferCache.TryGetValue(type, out var cached) &&
           ReferenceEquals(cached.buffer, buffer);

    private void RegisterIndexBufferReadyCallback(
        EPrimitiveType type,
        IndexBufferBuildTicket ticket,
        Action<XRDataBuffer, IndexSize> onReady)
        => _ = ticket.Completion.Task.ContinueWith(
            completed =>
            {
                if (completed.Status != TaskStatus.RanToCompletion)
                    return;

                try
                {
                    (XRDataBuffer buffer, IndexSize elementSize) = completed.Result;
                    lock (_indexBufferLock)
                    {
                        if (IsDestroyed || !IsCurrentIndexBufferBuildNoLock(type, ticket, buffer))
                            return;
                    }
                    // External callbacks never run under the cache lock. Renderer
                    // callbacks only publish a readiness edge and re-resolve on owner.
                    onReady(buffer, elementSize);
                }
                catch (Exception ex)
                {
                    RuntimeRenderingHostServices.Diagnostics.LogException(ex);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void BuildIndexBufferWorker(
        EPrimitiveType type,
        IndexBufferBuildTicket ticket,
        int[] indices,
        int vertexCount)
    {
        // Invalidated work that has not started needs neither allocation nor publication.
        if (ticket.Completion.Task.IsCompleted)
            return;

        XRDataBuffer? buffer = null;
        IndexSize elementSize = IndexSize.TwoBytes;
        Exception? error = null;

        try
        {
            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            buffer = BuildIndexBuffer(type, EBufferTarget.ElementArrayBuffer, indices, vertexCount, out elementSize) ??
                throw new InvalidOperationException($"Mesh has no {type} index data to build.");

            lock (_indexBufferLock)
            {
                if (!_indexBufferBuildTickets.TryGetValue(type, out IndexBufferBuildTicket? current) ||
                    !ReferenceEquals(current, ticket) ||
                    IsDestroyed ||
                    ticket.GeometryRevision != GeometryRevision)
                {
                    if (ReferenceEquals(current, ticket))
                        _indexBufferBuildTickets.Remove(type);
                    ticket.Fail(new IndexBufferBuildInvalidatedException());
                    return;
                }

                publication.Complete();
                _indexBufferCache[type] = (buffer, elementSize);
                ticket.Completion.TrySetResult((buffer, elementSize));
                return;
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        lock (_indexBufferLock)
        {
            ticket.Fail(error!);
        }

        RuntimeRenderingHostServices.Diagnostics.LogException(error!);

    }

    private void InvalidateBuildTicketNoLock(EPrimitiveType type)
    {
        if (!_indexBufferBuildTickets.Remove(type, out IndexBufferBuildTicket? ticket))
            return;

        ticket.Fail(new IndexBufferBuildInvalidatedException());
    }

    private XRDataBuffer? BuildIndexBuffer(EPrimitiveType type, EBufferTarget target, out IndexSize elementSize)
        => BuildIndexBuffer(type, target, GetIndices(type), VertexCount, out elementSize);

    private static XRDataBuffer? BuildIndexBuffer(
        EPrimitiveType type,
        EBufferTarget target,
        int[]? indices,
        int vertexCount,
        out IndexSize elementSize)
    {
        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        elementSize = IndexSize.TwoBytes;

        if (indices is null || indices.Length == 0)
            return null;

        var buf = new XRDataBuffer(target, true)
        {
            AttributeName = type.ToString()
        };

        // Use UInt16 as minimum index size to avoid dependency on VK_EXT_index_type_uint8.
        // Byte-sized indices are an optional Vulkan extension and the memory savings are negligible.
        if (vertexCount < short.MaxValue)
        {
            elementSize = IndexSize.TwoBytes;
            buf.SetDataRaw(indices.Select(x => (ushort)x), indices.Length);
        }
        else
        {
            elementSize = IndexSize.FourBytes;
            buf.SetDataRaw(indices);
        }

        publication.Complete();
        return buf;
    }

    private static void DisposeIndexBuffer(XRDataBuffer buffer)
    {
        buffer.Destroy(now: true);
        buffer.Dispose();
    }

    public bool PopulateIndexBuffer(EPrimitiveType type, XRDataBuffer buffer, IndexSize elementSize)
    {
        if (buffer == null || buffer.Length == 0)
            return false;

        var indices = GetIndices(type);
        if (indices is null || indices.Length == 0)
            return false;

        switch (elementSize)
        {
            case IndexSize.Byte:
                buffer.SetDataRaw(indices.Select(x => (byte)x), indices.Length);
                break;
            case IndexSize.TwoBytes:
                buffer.SetDataRaw(indices.Select(x => (ushort)x), indices.Length);
                break;
            default:
                buffer.SetDataRaw(indices);
                break;
        }

        return true;
    }
}
