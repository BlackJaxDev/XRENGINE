using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public XRMesh Clone()
    {
        using var _ = Engine.Profiler.Start("XRMesh Clone");

        XRMesh clone = new()
        {
            _interleaved = Interleaved,
            _interleavedStride = InterleavedStride,
            _positionOffset = PositionOffset,
            _normalOffset = NormalOffset,
            _tangentOffset = TangentOffset,
            _colorOffset = ColorOffset,
            _texCoordOffset = TexCoordOffset,
            _colorCount = ColorCount,
            _texCoordCount = TexCoordCount,
            VertexCount = VertexCount,
            _type = Type,
            _bounds = Bounds,
            _maxWeightCount = MaxWeightCount,
            BlendshapeNames = [.. BlendshapeNames],
            _vertices = new Vertex[Vertices.Length]
        };

        for (int i = 0; i < Vertices.Length; i++)
            clone._vertices[i] = Vertices[i].HardCopy();

        if (_points != null) clone._points = [.. _points];
        if (_lines != null) clone._lines = [.. _lines];
        if (_triangles != null) clone._triangles = [.. _triangles];

        clone.Buffers = Buffers.Clone();

        clone.PositionsBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.Position.ToString());
        clone.NormalsBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.Normal.ToString());
        clone.TangentsBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.Tangent.ToString());

        if (ColorBuffers != null)
        {
            clone.ColorBuffers = new XRDataBuffer[ColorBuffers.Length];
            for (int i = 0; i < ColorBuffers.Length; i++)
                clone.ColorBuffers[i] = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.Color}{i}")!;
        }

        if (TexCoordBuffers != null)
        {
            clone.TexCoordBuffers = new XRDataBuffer[TexCoordBuffers.Length];
            for (int i = 0; i < TexCoordBuffers.Length; i++)
                clone.TexCoordBuffers[i] = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.TexCoord}{i}")!;
        }

        clone.InterleavedVertexBuffer = clone.Buffers.GetValueOrDefault(ECommonBufferType.InterleavedVertex.ToString());

        if (HasSkinning)
        {
            clone.UtilizedBones = new (TransformBase tfm, System.Numerics.Matrix4x4 invBindWorldMtx)[UtilizedBones.Length];
            Array.Copy(UtilizedBones, clone.UtilizedBones, UtilizedBones.Length);
            clone.BoneWeightOffsets = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneMatrixOffset.ToString());
            clone.BoneWeightCounts = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneMatrixCount.ToString());
            clone.BoneWeightIndices = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BoneMatrixIndices}Buffer");
            clone.BoneWeightValues = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BoneMatrixWeights}Buffer");
        }

        if (HasBlendshapes)
        {
            clone.BlendshapeCounts = clone.Buffers.GetValueOrDefault(ECommonBufferType.BlendshapeCount.ToString());
            clone.BlendshapeIndices = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeIndices}Buffer");
            clone.BlendshapeDeltas = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeDeltas}Buffer");
        }

        return clone;
    }
}