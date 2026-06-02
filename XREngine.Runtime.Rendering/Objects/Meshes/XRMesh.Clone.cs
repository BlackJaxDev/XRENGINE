using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public XRMesh Clone()
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope("XRMesh Clone");

        XRMesh clone = new()
        {
            Name = Name,
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
            _patchVertices = PatchVertices,
            _bounds = Bounds,
            _maxWeightCount = MaxWeightCount,
            _skinningShaderConvention = SkinningShaderConvention,
            _skinningInfluenceEncoding = SkinningInfluenceEncoding,
            _skinningCoreIndexFormat = SkinningCoreIndexFormat,
            _hasSpillInfluences = HasSpillInfluences,
            _maxSpillInfluenceCount = MaxSpillInfluenceCount,
            BindRootMatrix = BindRootMatrix,
            BlendshapeNames = [.. BlendshapeNames],
            _blendshapeShaderVariant = BlendshapeShaderVariant,
            _blendshapeDeltaStorageMode = BlendshapeDeltaStorageMode,
            _blendshapeDeltaEncoding = BlendshapeDeltaEncoding,
            _blendshapeAffectedVertexCount = BlendshapeAffectedVertexCount,
            _blendshapeSparseRecordCount = BlendshapeSparseRecordCount,
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
            clone.BoneInfluenceCoreIndices = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceCoreIndices.ToString());
            clone.BoneInfluenceCoreWeights = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceCoreWeights.ToString());
            clone.BoneInfluenceSpillHeaders = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceSpillHeaders.ToString());
            clone.BoneInfluenceSpillEntries = clone.Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceSpillEntries.ToString());
        }

        if (HasBlendshapes)
        {
            clone.BlendshapeCounts = clone.Buffers.GetValueOrDefault(ECommonBufferType.BlendshapeCount.ToString());
            clone.BlendshapeIndices = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeIndices}Buffer");
            clone.BlendshapeDeltas = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeDeltas}Buffer");
            clone.BlendshapeSparseShapeRanges = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeSparseShapeRanges}Buffer");
            clone.BlendshapeSparseRecords = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeSparseRecords}Buffer");
            clone.BlendshapeQuantizedDeltas = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeQuantizedDeltas}Buffer");
            clone.BlendshapeQuantizationMetadata = clone.Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeQuantizationMetadata}Buffer");
        }

        return clone;
    }

    internal XRMesh CloneForRuntimeTransformRebind()
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope("XRMesh Runtime Transform Rebind Clone");

        XRMesh clone = new()
        {
            Name = Name,
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
            _patchVertices = PatchVertices,
            _bounds = Bounds,
            _maxWeightCount = MaxWeightCount,
            _skinningShaderConvention = SkinningShaderConvention,
            _skinningInfluenceEncoding = SkinningInfluenceEncoding,
            _skinningCoreIndexFormat = SkinningCoreIndexFormat,
            _hasSpillInfluences = HasSpillInfluences,
            _maxSpillInfluenceCount = MaxSpillInfluenceCount,
            BindRootMatrix = BindRootMatrix,
            BlendshapeNames = [.. BlendshapeNames],
            _blendshapeShaderVariant = BlendshapeShaderVariant,
            _blendshapeDeltaStorageMode = BlendshapeDeltaStorageMode,
            _blendshapeDeltaEncoding = BlendshapeDeltaEncoding,
            _blendshapeAffectedVertexCount = BlendshapeAffectedVertexCount,
            _blendshapeSparseRecordCount = BlendshapeSparseRecordCount,
            _vertices = Vertices,
            _points = _points,
            _lines = _lines,
            _triangles = _triangles,
        };

        if (HasSkinning)
        {
            clone.UtilizedBones = new (TransformBase tfm, System.Numerics.Matrix4x4 invBindWorldMtx)[UtilizedBones.Length];
            Array.Copy(UtilizedBones, clone.UtilizedBones, UtilizedBones.Length);
        }

        clone.Buffers = Buffers.CloneShared();
        return clone;
    }
}
