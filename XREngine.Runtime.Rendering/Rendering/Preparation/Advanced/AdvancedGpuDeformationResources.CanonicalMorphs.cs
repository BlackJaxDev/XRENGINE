using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    /// <summary>Cooked meshes retain sparse quantized morphs separately from their rebuilt legacy vertices.</summary>
    private static bool TryInitializeCanonicalMorphs(PendingGpuDeformationMeshPreparation pending)
    {
        if (pending.ActiveBlendshapeCount == 0) return false;
        XRMeshBlendshapeBufferState state = pending.Mesh.GetBlendshapeBufferStateSnapshot();
        if (state.QuantizedDeltas is null && state.QuantizationMetadata is null) return false;
        if (state.QuantizedDeltas is not { ComponentType: EComponentType.UInt, ComponentCount: 2, Length: >= 8 } deltas ||
            state.QuantizationMetadata is not { ComponentType: EComponentType.Float, ComponentCount: 4 } metadata ||
            metadata.Length != checked((uint)pending.ActiveBlendshapeCount * 64u) ||
            deltas.Length % 8 != 0 || state.Encoding != BlendshapeDeltaEncoding.Snorm16Vector3 ||
            ReadCanonicalMorphWord(deltas, 0) != 0 || ReadCanonicalMorphWord(deltas, 1) != 0)
            throw new NotSupportedException("Aggregate deformation requires the canonical finite Snorm16 sparse morph encoding.");
        bool identity = state.SparseRecordCount == 0 && state.SparseShapeRanges is null && state.SparseRecords is null;
        if (!identity && (state.SparseShapeRanges is not { ComponentCount: 4 } ranges ||
            ranges.Length != checked((uint)pending.ActiveBlendshapeCount * 16u) ||
            state.SparseRecords is not { ComponentCount: 4 } records || records.Length % 16 != 0))
            throw new NotSupportedException("Aggregate deformation requires exact canonical sparse morph ranges and records.");
        pending.CanonicalMorphs = state;
        pending.MorphRangesRevision = state.SparseShapeRanges?.Revision ?? 0;
        pending.MorphRecordsRevision = state.SparseRecords?.Revision ?? 0;
        pending.MorphDeltasRevision = deltas.Revision;
        pending.MorphMetadataRevision = metadata.Revision;
        pending.Stage = PendingGpuDeformationMeshPreparation.CanonicalMorphCount;
        return true;
    }

    private static bool MatchesCanonicalMorphWitness(PendingGpuDeformationMeshPreparation pending)
    {
        XRMeshBlendshapeBufferState? state = pending.CanonicalMorphs;
        return state is null || ReferenceEquals(state, pending.Mesh.GetBlendshapeBufferStateSnapshot()) &&
            (state.SparseShapeRanges?.Revision ?? 0) == pending.MorphRangesRevision &&
            (state.SparseRecords?.Revision ?? 0) == pending.MorphRecordsRevision &&
            (state.QuantizedDeltas?.Revision ?? 0) == pending.MorphDeltasRevision &&
            (state.QuantizationMetadata?.Revision ?? 0) == pending.MorphMetadataRevision;
    }

    private static void AdvanceCanonicalMorphPreparation(PendingGpuDeformationMeshPreparation pending)
    {
        XRMeshBlendshapeBufferState state = pending.CanonicalMorphs!;
        if (!MatchesCanonicalMorphWitness(pending))
            throw new InvalidOperationException("The canonical sparse morph generation changed during aggregate preparation.");
        if (pending.Stage == PendingGpuDeformationMeshPreparation.CanonicalMorphCount)
        {
            if (pending.ShapeIndex < pending.ActiveBlendshapeCount)
            {
                // Metadata is part of every shape, including identity shapes with no sparse records.
                for (uint vector = 0; vector < 4; vector++)
                    _ = ReadCanonicalMorphVector(state.QuantizationMetadata!, checked((uint)pending.ShapeIndex * 4 + vector));
                ReadCanonicalMorphRange(pending, pending.ShapeIndex++, out _, out uint count);
                pending.RecordCount = checked(pending.RecordCount + count);
                return;
            }
            pending.RecordsScratch = new AdvancedBlendshapeSparseRecord[checked((int)pending.RecordCount)];
            pending.DeltasScratch = new Vector4[checked((int)pending.RecordCount * 3 + 1)];
            pending.Stage = PendingGpuDeformationMeshPreparation.CanonicalMorphPack;
            pending.ShapeIndex = 0;
        }
        if (pending.ShapeIndex >= pending.ActiveBlendshapeCount)
        {
            pending.DeltaCount = pending.PackedDeltaCount;
            if (pending.DeltasScratch.Length != pending.PackedDeltaCount)
                Array.Resize(ref pending.DeltasScratch, checked((int)pending.PackedDeltaCount));
            pending.Stage = PendingGpuDeformationMeshPreparation.Vertices;
            pending.VertexIndex = 0;
            return;
        }
        int shape = pending.ShapeIndex;
        ReadCanonicalMorphRange(pending, shape, out uint first, out uint length);
        if (pending.VertexIndex == 0)
            pending.RangesScratch[shape] = new(pending.PackedRecordCount, length, 0, 0);
        if (pending.VertexIndex >= length)
        {
            pending.ShapeIndex++;
            pending.VertexIndex = 0;
            return;
        }
        uint source = checked((first + (uint)pending.VertexIndex) * 4);
        XRDataBuffer records = state.SparseRecords!;
        uint vertex = ReadCanonicalMorphWord(records, source);
        if (vertex >= pending.VertexCount || pending.VertexIndex != 0 &&
            vertex <= ReadCanonicalMorphWord(records, source - 4))
            throw new NotSupportedException("Canonical sparse morph records must contain sorted unique in-range vertices.");
        AdvancedBlendshapeRange range = pending.RangesScratch[shape];
        uint flags = range.AttributeFlags;
        uint p = AppendCanonicalMorphDelta(pending, shape, ReadCanonicalMorphWord(records, source + 1), 1, ref flags);
        uint n = AppendCanonicalMorphDelta(pending, shape, ReadCanonicalMorphWord(records, source + 2), 2, ref flags);
        uint t = AppendCanonicalMorphDelta(pending, shape, ReadCanonicalMorphWord(records, source + 3), 4, ref flags);
        pending.RecordsScratch[pending.PackedRecordCount++] = new(vertex, p, n, t);
        pending.RangesScratch[shape] = range with { AttributeFlags = flags };
        pending.VertexIndex++;
    }

    private static void ReadCanonicalMorphRange(PendingGpuDeformationMeshPreparation pending, int shape, out uint first, out uint count)
    {
        XRMeshBlendshapeBufferState state = pending.CanonicalMorphs!;
        first = count = 0;
        // Identity morphs retain quantization metadata and the null delta only.
        if (state.SparseShapeRanges is null) return;
        first = ReadCanonicalMorphWord(state.SparseShapeRanges, checked((uint)shape * 4));
        count = ReadCanonicalMorphWord(state.SparseShapeRanges, checked((uint)shape * 4 + 1));
        uint available = state.SparseRecords!.Length / 16;
        if (first > available || count > available - first)
            throw new NotSupportedException("Canonical sparse morph ranges exceed their exact source records.");
    }

    private static uint AppendCanonicalMorphDelta(PendingGpuDeformationMeshPreparation pending, int shape,
        uint index, uint flag, ref uint flags)
    {
        if (index == 0) return 0;
        XRMeshBlendshapeBufferState state = pending.CanonicalMorphs!;
        XRDataBuffer deltas = state.QuantizedDeltas!;
        if (index >= deltas.Length / 8)
            throw new NotSupportedException("A canonical morph delta index exceeds its exact source range.");
        uint xy = ReadCanonicalMorphWord(deltas, checked(index * 2));
        uint z = ReadCanonicalMorphWord(deltas, checked(index * 2 + 1));
        Vector4 scale4 = ReadCanonicalMorphVector(state.QuantizationMetadata!, checked((uint)shape * 4 + 2));
        Vector4 bias4 = ReadCanonicalMorphVector(state.QuantizationMetadata!, checked((uint)shape * 4 + 3));
        Vector3 delta = BlendshapeDeltaQuantizer.DecodeSnorm16(
            (unchecked((short)xy), unchecked((short)(xy >> 16)), unchecked((short)z)),
            new(scale4.X, scale4.Y, scale4.Z), new(bias4.X, bias4.Y, bias4.Z));
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
            throw new NotSupportedException("Canonical morph quantization must produce finite attribute deltas.");
        // Preserve even tiny authored quantized deltas; the zero index alone denotes an absent delta.
        uint result = pending.PackedDeltaCount++;
        pending.DeltasScratch[result] = new(delta, 0);
        flags |= flag;
        return result;
    }

    private static unsafe uint ReadCanonicalMorphWord(XRDataBuffer buffer, uint word)
    {
        VoidPtr address = RequireCanonicalMorphInput(buffer);
        if (word >= buffer.Length / 4)
            throw new NotSupportedException("A canonical sparse morph word exceeds its exact source extent.");
        uint bits = ((uint*)address.Pointer)[word];
        if (buffer.ComponentType == EComponentType.UInt) return bits;
        if (buffer.ComponentType == EComponentType.Int && unchecked((int)bits) >= 0) return bits;
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (buffer.ComponentType == EComponentType.Float && float.IsFinite(value) && value >= 0 &&
            value < uint.MaxValue && value == MathF.Truncate(value)) return checked((uint)value);
        throw new NotSupportedException("Canonical sparse morph indices must be exact nonnegative integers.");
    }

    private static unsafe Vector4 ReadCanonicalMorphVector(XRDataBuffer buffer, uint vector)
    {
        VoidPtr address = RequireCanonicalMorphInput(buffer);
        if (buffer.ComponentType != EComponentType.Float || vector >= buffer.Length / 16)
            throw new NotSupportedException("Canonical morph metadata requires exact float4 records.");
        Vector4 value = ((Vector4*)address.Pointer)[vector];
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new NotSupportedException("Canonical morph metadata must be finite.");
        return value;
    }

    private static VoidPtr RequireCanonicalMorphInput(XRDataBuffer buffer)
    {
        if (buffer.GpuProduced || buffer.IsDestroyed || buffer.ClientSideSource is not { } source ||
            source.Length < buffer.Length || !buffer.TryGetAddress(out VoidPtr address) || address == VoidPtr.Zero)
            throw new NotSupportedException("Canonical sparse morph packing requires retained CPU-authored input records.");
        return address;
    }
}
