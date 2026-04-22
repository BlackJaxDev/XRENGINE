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
    private readonly HashSet<EPrimitiveType> _indexBufferBuilding = new();
    private readonly Dictionary<EPrimitiveType, List<Action<XRDataBuffer, IndexSize>>> _indexBufferPendingCallbacks = new();

    /// <summary>
    /// Clears the cached index buffers so the next call to
    /// <see cref="GetIndexBuffer"/> will recreate them.
    /// Pass <c>null</c> to clear all, or a specific type to clear just that.
    /// </summary>
    public void InvalidateIndexBufferCache(EPrimitiveType? type = null)
    {
        lock (_indexBufferLock)
        {
            if (type.HasValue)
                _indexBufferCache.Remove(type.Value);
            else
                _indexBufferCache.Clear();
        }
    }

    // Indices API
    public int[]? GetIndices()
        => GetIndices(Type);

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
            return remapper;
        }
        for (int i = 0; i < vertices.Length;)
            _triangles.Add(new IndexTriangle(i++, i++, i++));
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
            return remapper;
        }
        for (int i = 0; i < vertices.Length;)
            _lines.Add(new IndexLine(i++, i++));
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
            return remapper;
        }
        for (int i = 0; i < vertices.Length;)
            _points.Add(i++);
        return null;
    }

    // BVH / Intersection
    public BVH<Triangle>? BVHTree
    {
        get
        {
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
            RuntimeRenderingHostServices.Current.LogException(ex);
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
        if (BVHTree is null) return null;

        var matches = BVHTree.Traverse(x => GeoUtil.Intersect.SegmentWithAABB(localSegment.Start, localSegment.End, x.Min, x.Max, out _, out _));
        if (matches is null) return null;

        float? minDist = null;
        foreach (var node in matches)
        {
            if (node.gobjects is null || node.gobjects.Count == 0)
                continue;
            var tri = node.gobjects[0];
            GeoUtil.Intersect.RayWithTriangle(localSegment.Start, localSegment.End, tri.A, tri.B, tri.C, out float dist);
            if (minDist is null || dist < minDist)
            {
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
        RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(() =>
        {
            const int ls = 8;
            RuntimeRenderingHostServices.Current.DispatchCompute(
                program,
                (uint)((resolution.X + ls - 1) / ls),
                (uint)((resolution.Y + ls - 1) / ls),
                (uint)((resolution.Z + ls - 1) / ls));
        });
    }

    // Index buffer helpers
    /// <summary>
    /// Returns a GPU-ready index buffer for the given primitive type.
    ///
    /// For the default <see cref="EBufferTarget.ElementArrayBuffer"/> target, the heavy index
    /// conversion work (traversing triangle/line/point lists, downcasting to ushort, uploading
    /// the raw bytes) is offloaded to a background <see cref="Task.Run"/> so it never stalls the
    /// render thread. The first call after an invalidation returns <c>null</c> and schedules the
    /// build; once the background task finishes, the buffer is cached and any <paramref name="onReady"/>
    /// callbacks are invoked. Subsequent calls return the cached buffer synchronously.
    ///
    /// Non-default targets (e.g. <see cref="EBufferTarget.ShaderStorageBuffer"/> for compute) are
    /// rare and off the render hot path, so they build synchronously and bypass the cache.
    /// </summary>
    /// <param name="onReady">
    /// Optional completion callback. Invoked synchronously if the buffer is already cached, or on
    /// the background task thread when the async build completes. Callers that need the callback
    /// on a specific thread should marshal inside the callback body.
    /// </param>
    public XRDataBuffer? GetIndexBuffer(
        EPrimitiveType type,
        out IndexSize elementSize,
        EBufferTarget target = EBufferTarget.ElementArrayBuffer,
        Action<XRDataBuffer, IndexSize>? onReady = null)
    {
        elementSize = IndexSize.TwoBytes;

        // Non-default targets bypass the cache and build synchronously.
        if (target != EBufferTarget.ElementArrayBuffer)
        {
            var syncBuf = BuildIndexBuffer(type, target, out elementSize);
            if (syncBuf is not null)
                onReady?.Invoke(syncBuf, elementSize);
            return syncBuf;
        }

        // Cheap short-circuit: avoid spawning a Task just to discover there are no indices
        // (e.g. querying Points on a triangle-only mesh).
        if (!HasAnyIndices(type))
            return null;

        lock (_indexBufferLock)
        {
            if (_indexBufferCache.TryGetValue(type, out var cached))
            {
                elementSize = cached.elementSize;
                onReady?.Invoke(cached.buffer, cached.elementSize);
                return cached.buffer;
            }

            if (onReady is not null)
            {
                if (!_indexBufferPendingCallbacks.TryGetValue(type, out var list))
                    _indexBufferPendingCallbacks[type] = list = new List<Action<XRDataBuffer, IndexSize>>();
                list.Add(onReady);
            }

            if (_indexBufferBuilding.Add(type))
            {
                var buildType = type;
                Task.Run(() => BuildIndexBufferWorker(buildType));
            }
        }

        return null;
    }

    private bool HasAnyIndices(EPrimitiveType type) => type switch
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

    private void BuildIndexBufferWorker(EPrimitiveType type)
    {
        XRDataBuffer? buf = null;
        IndexSize elementSize = IndexSize.TwoBytes;
        Exception? error = null;

        try
        {
            buf = BuildIndexBuffer(type, EBufferTarget.ElementArrayBuffer, out elementSize);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        List<Action<XRDataBuffer, IndexSize>>? callbacks;
        lock (_indexBufferLock)
        {
            if (buf is not null)
                _indexBufferCache[type] = (buf, elementSize);
            _indexBufferBuilding.Remove(type);
            _indexBufferPendingCallbacks.Remove(type, out callbacks);
        }

        if (error is not null)
            RuntimeRenderingHostServices.Current.LogException(error);

        if (buf is null || callbacks is null)
            return;

        for (int i = 0; i < callbacks.Count; i++)
        {
            try
            {
                callbacks[i].Invoke(buf, elementSize);
            }
            catch (Exception ex)
            {
                RuntimeRenderingHostServices.Current.LogException(ex);
            }
        }
    }

    private XRDataBuffer? BuildIndexBuffer(EPrimitiveType type, EBufferTarget target, out IndexSize elementSize)
    {
        elementSize = IndexSize.TwoBytes;

        var indices = GetIndices(type);
        if (indices is null || indices.Length == 0)
            return null;

        var buf = new XRDataBuffer(target, true)
        {
            AttributeName = type.ToString()
        };

        // Use UInt16 as minimum index size to avoid dependency on VK_EXT_index_type_uint8.
        // Byte-sized indices are an optional Vulkan extension and the memory savings are negligible.
        if (VertexCount < short.MaxValue)
        {
            elementSize = IndexSize.TwoBytes;
            buf.SetDataRaw(indices.Select(x => (ushort)x), indices.Length);
        }
        else
        {
            elementSize = IndexSize.FourBytes;
            buf.SetDataRaw(indices);
        }

        return buf;
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
