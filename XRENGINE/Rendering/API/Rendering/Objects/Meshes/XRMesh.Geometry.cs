using System.Numerics;
using SimpleScene.Util.ssBVH;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMesh
{
    // Indices API
    public int[]? GetIndices()
        => GetIndices(Type);

    public int[]? GetIndices(EPrimitiveType type) => type switch
    {
        EPrimitiveType.Triangles => _triangles?.SelectMany(x => new[] { x.Point0, x.Point1, x.Point2 }).ToArray(),
        EPrimitiveType.Lines => _lines?.SelectMany(x => new[] { x.Point0, x.Point1 }).ToArray(),
        EPrimitiveType.Points => _points?.Select(x => (int)x).ToArray(),
        _ => null
    };

    private Remapper? SetTriangleIndices(Vertex[] vertices, bool remap = true)
    {
        _triangles = [];
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
        _lines = [];
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
        _points = [];
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
            if (_bvhTree is null && !_generating)
            {
                _generating = true;
                Task.Run(GenerateBVH);
            }
            return _bvhTree;
        }
    }

    public void GenerateBVH()
    {
        if (Triangles is null)
            return;
        _bvhTree = new(new TriangleAdapter(), [.. Triangles.Select(GetTriangle)]);
        _generating = false;
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

        var matches = BVHTree.Traverse(x => GeoUtil.SegmentIntersectsAABB(localSegment.Start, localSegment.End, x.Min, x.Max, out _, out _));
        if (matches is null) return null;

        float? minDist = null;
        foreach (var node in matches)
        {
            if (node.gobjects is null || node.gobjects.Count == 0)
                continue;
            var tri = node.gobjects[0];
            GeoUtil.RayIntersectsTriangle(localSegment.Start, localSegment.End, tri.A, tri.B, tri.C, out float dist);
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
        Engine.EnqueueMainThreadTask(() =>
        {
            const int ls = 8;
            AbstractRenderer.Current?.DispatchCompute(
                program,
                (resolution.X + ls - 1) / ls,
                (resolution.Y + ls - 1) / ls,
                (resolution.Z + ls - 1) / ls);
        });
    }

    // Index buffer helpers
    public XRDataBuffer? GetIndexBuffer(EPrimitiveType type, out IndexSize elementSize, EBufferTarget target = EBufferTarget.ElementArrayBuffer)
    {
        elementSize = IndexSize.Byte;

        var indices = GetIndices(type);
        if (indices is null || indices.Length == 0)
            return null;

        var buf = new XRDataBuffer(target, true)
        {
            AttributeName = type.ToString()
        };

        if (VertexCount < byte.MaxValue)
        {
            elementSize = IndexSize.Byte;
            buf.SetDataRaw(indices.Select(x => (byte)x), indices.Length);
        }
        else if (VertexCount < short.MaxValue)
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