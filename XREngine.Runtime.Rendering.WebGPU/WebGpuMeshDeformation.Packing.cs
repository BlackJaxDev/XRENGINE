using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuMeshDeformation
{
    private unsafe XRDataBuffer CreatePackedInput()
    {
        // Preserve the existing canonical packed kernel's header and record encoding.
        // The two unused UV words remain zero; authored UV/color streams are read directly by raster.
        Span<uint> header = stackalloc uint[64];
        header.Clear();
        header[0] = 0x534b5258;
        header[1] = 1;
        header[2] = checked((uint)_mesh.VertexCount);
        header[3] = _paletteCount;
        header[4] = _skinning ? (uint)_skinningState.CoreIndexFormat : 1;
        header[5] = (uint)((_mesh.HasNormals ? 1 : 0) | (_mesh.HasTangents ? 2 : 0) | (_skinning ? 4 : 0) |
            (_maximumMorphAccumulation ? 8 : 0) | (_skinning && _skinningState.HasSpillInfluences ? 16 : 0));
        header[6] = header[7] = _blendshapes ? _mesh.BlendshapeCount : 0;
        header[19] = uint.MaxValue;
        header[24] = BitConverter.SingleToUInt32Bits(0.0001f);
        int cursor = 256;
        AddVertexSource(header, 8, "position", 5, ref cursor);
        if (_mesh.HasNormals) AddVertexSource(header, 9, "normal", 3, ref cursor);
        if (_mesh.HasTangents) AddVertexSource(header, 10, "tangent", 4, ref cursor);
        if (_skinning)
        {
            AddRawSource(header, 11, _skinningState.CoreIndices, checked(_mesh.VertexCount * (int)header[4] * 4), false, ref cursor);
            AddRawSource(header, 12, _skinningState.CoreWeights, checked(_mesh.VertexCount * 4), false, ref cursor);
            if (_skinningState.HasSpillInfluences)
            {
                AddRawSource(header, 13, _skinningState.SpillHeaders, checked(_mesh.VertexCount * 4), false, ref cursor);
                AddRawSource(header, 14, _skinningState.SpillEntries, -1, false, ref cursor);
                header[22] = _skinningState.SpillEntries!.Length / 4;
            }
        }
        if (_blendshapes)
        {
            if (_blendshapeState.QuantizedDeltas is not { ComponentType: EComponentType.UInt, ComponentCount: 2 } ||
                _blendshapeState.QuantizationMetadata is not { ComponentType: EComponentType.Float, ComponentCount: 4 })
                throw Unsupported("canonical quantized morph deltas require uint2 pairs and float4 metadata");
            if (_hasMorphRecords)
            {
                if (_blendshapeState.SparseShapeRanges is not { ComponentCount: 4 } ||
                    _blendshapeState.SparseRecords is not { ComponentCount: 4 })
                    throw Unsupported("canonical sparse morph ranges and records require four logical components");
                AddRawSource(header, 15, _blendshapeState.SparseShapeRanges, checked((int)_mesh.BlendshapeCount * 16), true, ref cursor);
                AddRawSource(header, 16, _blendshapeState.SparseRecords, -1, true, ref cursor);
                header[20] = _blendshapeState.SparseRecords.Length / 16;
            }
            else
            {
                // The canonical builder deliberately omits both sparse buffers for
                // identity morphs. Supply zero ranges without inventing any deltas.
                header[15] = checked((uint)cursor / 4);
                cursor = checked(cursor + (int)_mesh.BlendshapeCount * 16);
                header[16] = checked((uint)cursor / 4);
            }
            AddRawSource(header, 17, _blendshapeState.QuantizedDeltas, -1, false, ref cursor);
            AddRawSource(header, 18, _blendshapeState.QuantizationMetadata, checked((int)_mesh.BlendshapeCount * 64), false, ref cursor);
            if ((_blendshapeState.SparseRecords?.Length ?? 0) % 16 != 0 || _blendshapeState.QuantizedDeltas.Length % 8 != 0)
                throw Unsupported("the sparse morph records, quantized delta pairs or metadata have an invalid layout");
            header[21] = _blendshapeState.QuantizedDeltas.Length / 8;
        }
        if (cursor > 8 * 1024 * 1024)
            throw Unsupported("the packed canonical inputs exceed the bounded 8 MiB storage profile");
        header[23] = (uint)cursor / 4;
        XRDataBuffer packed = new("PackedSkinningData", EBufferTarget.ShaderStorageBuffer, (uint)cursor / 4,
            EComponentType.UInt, 1, false, true) { DisposeOnPush = false, Resizable = false };
        Span<byte> bytes = new(packed.Address.Pointer, cursor);
        bytes.Clear();
        MemoryMarshal.AsBytes(header).CopyTo(bytes);
        foreach (WebGpuDeformationSource source in _sources) source.CopyIfChanged(bytes);
        ValidatePackedRecords(MemoryMarshal.Cast<byte, uint>(bytes));
        return packed;
    }

    private void AddVertexSource(Span<uint> header, int headerIndex, string semantic, int outputComponents, ref int cursor)
    {
        XRDataBuffer? source;
        int offset;
        int stride;
        int components = 3;
        if (_mesh.Interleaved)
        {
            source = _mesh.InterleavedVertexBuffer;
            uint? sourceOffset = semantic switch
            {
                "position" => _mesh.PositionOffset,
                "normal" => _mesh.NormalOffset,
                _ => _mesh.TangentOffset,
            };
            if (sourceOffset is null) throw Unsupported("a required interleaved deformation attribute is absent");
            offset = checked((int)sourceOffset.Value);
            stride = checked((int)_mesh.InterleavedStride);
            if (semantic == "tangent") components = 4;
        }
        else
        {
            source = semantic switch { "position" => _mesh.PositionsBuffer, "normal" => _mesh.NormalsBuffer, _ => _mesh.TangentsBuffer };
            offset = 0;
            stride = checked((int)(source?.ElementSize ?? 0));
            if (source?.ComponentType != EComponentType.Float || source.ComponentCount is not (3 or 4))
                throw Unsupported("deformation attributes require canonical float source records");
            if (semantic == "tangent" && source.ComponentCount == 4) components = 4;
        }
        if (source is null) throw Unsupported("a required deformation source stream is absent");
        int length = checked(_mesh.VertexCount * outputComponents * 4);
        header[headerIndex] = checked((uint)cursor / 4);
        _sources.Add(new(source, cursor, length, vertexCount: _mesh.VertexCount, sourceOffset: offset,
            sourceStride: stride, sourceComponents: components, outputComponents: outputComponents));
        cursor = checked(cursor + length);
    }

    private void AddRawSource(Span<uint> header, int headerIndex, XRDataBuffer? source, int byteCount, bool logicalIndices, ref int cursor)
    {
        if (source is null) throw Unsupported("a required canonical skinning or sparse morph buffer is absent");
        int length = byteCount < 0 ? checked((int)source.Length) : byteCount;
        if ((length & 3) != 0) throw Unsupported("canonical packed records must have whole-word lengths");
        header[headerIndex] = checked((uint)cursor / 4);
        _sources.Add(new(source, cursor, length, logicalIndices));
        cursor = checked(cursor + length);
    }

    private unsafe void RefreshPackedInputs()
    {
        Span<byte> bytes = new(_packed.Address.Pointer, checked((int)_packed.Length));
        foreach (WebGpuDeformationSource source in _sources)
            if (source.CopyIfChanged(bytes))
            {
                _packedDirtyStart = Math.Min(_packedDirtyStart, source.DestinationOffset);
                _packedDirtyEnd = Math.Max(_packedDirtyEnd, source.DestinationOffset + source.ByteCount);
            }
        if (_packedDirtyStart == int.MaxValue) return;
        ValidatePackedRecords(MemoryMarshal.Cast<byte, uint>(bytes));
        _packed.CommitDirtyBytes((uint)_packedDirtyStart, checked((uint)(_packedDirtyEnd - _packedDirtyStart)));
        _packedDirtyStart = int.MaxValue;
        _packedDirtyEnd = 0;
    }

    private void ValidatePackedRecords(ReadOnlySpan<uint> words)
    {
        int count = _mesh.VertexCount;
        if (_skinning)
        {
            int format = (int)words[4];
            if (format is not (1 or 2) || _paletteCount == 0) throw Unsupported("canonical core influences require a palette and Core4x8/Core4x16 indices");
            for (int vertex = 0; vertex < count; vertex++)
            {
                uint weights = words[(int)words[12] + vertex];
                for (int lane = 0; lane < 4; lane++)
                {
                    uint packed = words[(int)words[11] + vertex * format + (format == 1 ? 0 : lane / 2)];
                    uint bone = format == 1 ? (packed >> (lane * 8)) & 255u : (packed >> ((lane & 1) * 16)) & 65535u;
                    if (((weights >> (lane * 8)) & 255) != 0 && bone >= _paletteCount)
                        throw Unsupported("a core influence exceeds the canonical palette count");
                }
                if (_skinningState.HasSpillInfluences)
                {
                    uint header = words[(int)words[13] + vertex];
                    uint offset = header & 0xffffff;
                    uint length = header >> 24;
                    if (offset > words[22] || length > words[22] - offset)
                        throw Unsupported("a spill influence range exceeds its source records");
                }
            }
            for (int index = 0; index < words[22]; index++)
            {
                uint entry = words[(int)words[14] + index];
                if ((entry >> 24) != 0 || ((entry >> 16) & 255) != 0 && (entry & 65535) >= _paletteCount)
                    throw Unsupported("a spill influence exceeds the canonical palette or packed encoding");
            }
        }
        if (!_blendshapes) return;
        if (words[21] == 0 || words[(int)words[17]] != 0 || words[(int)words[17] + 1] != 0)
            throw Unsupported("quantized morph delta zero must be the null sentinel");
        ReadOnlySpan<float> metadata = MemoryMarshal.Cast<uint, float>(words.Slice((int)words[18], checked((int)words[6] * 16)));
        foreach (float value in metadata)
            if (!float.IsFinite(value)) throw Unsupported("morph quantization metadata must be finite");
        for (int shape = 0; shape < words[6]; shape++)
        {
            int range = (int)words[15] + shape * 4;
            uint start = words[range], length = words[range + 1];
            if (start > words[20] || length > words[20] - start)
                throw Unsupported("a sparse morph range exceeds its source records");
            long previous = -1;
            for (uint index = start; index < start + length; index++)
            {
                int record = checked((int)(words[16] + index * 4));
                uint vertex = words[record];
                if (vertex >= count || vertex <= previous)
                    throw Unsupported("sparse morph vertices must be unique and sorted within each shape");
                previous = vertex;
                for (int lane = 1; lane < 4; lane++)
                    if (words[record + lane] >= words[21])
                        throw Unsupported("a sparse morph delta index exceeds its source records");
            }
        }
    }

    private void ValidateActiveMorphs(XRDataBuffer active, uint count)
    {
        if (count == 0) return;
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(WebGpuDeformationSource.GetSourceBytes(active));
        float previous = -1;
        // The canonical active-list publisher iterates authored shape indices in order.
        // Validation preserves its uniqueness and bounds without allocating a visited set.
        for (int index = 0; index < count; index++)
        {
            float shape = values[index * 2], weight = values[index * 2 + 1];
            if (!float.IsFinite(shape) || shape != MathF.Truncate(shape) || shape <= previous ||
                shape >= _mesh.BlendshapeCount || !float.IsFinite(weight))
                throw Unsupported("the active morph list must contain sorted unique shape indices and finite weights");
            previous = shape;
        }
    }

    private static bool IsCanonicalIdentityMorphGeneration(XRMeshBlendshapeBufferState state, uint shapeCount)
        => state.SparseRecordCount == 0 && state.SparseShapeRanges is null && state.SparseRecords is null &&
            state.QuantizedDeltas is { ComponentType: EComponentType.UInt, ComponentCount: 2, Length: 8 } &&
            state.QuantizationMetadata is { ComponentType: EComponentType.Float, ComponentCount: 4 } metadata &&
            metadata.Length == checked(shapeCount * 64);
}
