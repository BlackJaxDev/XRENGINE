using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Cold-boundary packing adapter for the canonical Core4/spill and quantized sparse morph payloads.
/// This changes binding placement, not influence, palette, or morph encodings.
/// </summary>
public sealed partial class BrowserSkinningData
{
    private readonly byte[] _packet;

    /// <summary>Copies immutable engine records into a bounded backend binding arena.</summary>
    /// <remarks>
    /// Core index format is Core4x8 (1) or Core4x16 (2). Shape ranges contain four uints per shape;
    /// start/count address vertex-sorted records of vertex/position-delta/normal-delta/tangent-delta.
    /// Deltas contain two Snorm16-pair uints; entry zero is the null sentinel. Quantization metadata
    /// contains four vec4s per shape, with scale at index 2 and bias at index 3.
    /// </remarks>
    public BrowserSkinningData(BrowserMeshData mesh, int boneCount, int coreIndexFormat,
        ReadOnlySpan<uint> coreIndices, ReadOnlySpan<uint> coreWeights,
        ReadOnlySpan<Vector3> normals = default, ReadOnlySpan<Vector4> tangents = default,
        ReadOnlySpan<uint> spillHeaders = default, ReadOnlySpan<uint> spillEntries = default,
        ReadOnlySpan<uint> shapeRanges = default, ReadOnlySpan<uint> sparseRecords = default,
        ReadOnlySpan<uint> quantizedDeltas = default, ReadOnlySpan<Vector4> quantizationMetadata = default,
        int influenceCap = 259, bool maximumMorphAccumulation = false, float morphWeightThreshold = 0.0001f)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int count = mesh.VertexCount;
        if (count > 16384 || boneCount is < 0 or > 1024 || coreIndexFormat is not (1 or 2) ||
            coreIndices.Length != count * coreIndexFormat || coreWeights.Length != count ||
            (normals.Length != 0 && normals.Length != count) || (tangents.Length != 0 && tangents.Length != count) ||
            (spillHeaders.Length != 0 && spillHeaders.Length != count) || (spillHeaders.IsEmpty && !spillEntries.IsEmpty) ||
            shapeRanges.Length % 4 != 0 || shapeRanges.Length > 256 * 4 ||
            sparseRecords.Length % 4 != 0 || sparseRecords.Length > 65536 * 4 ||
            quantizedDeltas.Length % 2 != 0 || quantizedDeltas.Length > 65536 * 2 ||
            quantizationMetadata.Length != shapeRanges.Length || spillEntries.Length > 65536 ||
            influenceCap is < 1 or > 259 || !float.IsFinite(morphWeightThreshold) || morphWeightThreshold < 0)
            throw new ArgumentException("Compute skinning payload exceeds the bounded canonical layout.");
        BoneCount = boneCount;
        MorphCount = shapeRanges.Length / 4;
        byte[] vertices = mesh.CopyVertexBytes();
        int bytes = checked(256 + vertices.Length + normals.Length * 12 + tangents.Length * 16 +
            (coreIndices.Length + coreWeights.Length + spillHeaders.Length + spillEntries.Length +
             shapeRanges.Length + sparseRecords.Length + quantizedDeltas.Length) * 4 + quantizationMetadata.Length * 16);
        _packet = new byte[bytes];
        Write(0, 0x534b5258); Write(1, 1); Write(2, (uint)count); Write(3, (uint)boneCount);
        Write(4, (uint)coreIndexFormat);
        Write(5, (uint)((normals.IsEmpty ? 0 : 1) | (tangents.IsEmpty ? 0 : 2) |
            (boneCount == 0 ? 0 : 4) | (maximumMorphAccumulation ? 8 : 0) | (spillHeaders.IsEmpty ? 0 : 16)));
        Write(6, (uint)MorphCount); Write(7, (uint)MorphCount);
        int cursor = 256;
        Append(8, vertices, ref cursor);
        Append(9, MemoryMarshal.AsBytes(normals), ref cursor);
        Append(10, MemoryMarshal.AsBytes(tangents), ref cursor);
        Append(11, MemoryMarshal.AsBytes(coreIndices), ref cursor);
        Append(12, MemoryMarshal.AsBytes(coreWeights), ref cursor);
        Append(13, MemoryMarshal.AsBytes(spillHeaders), ref cursor);
        Append(14, MemoryMarshal.AsBytes(spillEntries), ref cursor);
        Append(15, MemoryMarshal.AsBytes(shapeRanges), ref cursor);
        Append(16, MemoryMarshal.AsBytes(sparseRecords), ref cursor);
        Append(17, MemoryMarshal.AsBytes(quantizedDeltas), ref cursor);
        Append(18, MemoryMarshal.AsBytes(quantizationMetadata), ref cursor);
        Write(19, (uint)influenceCap); Write(20, (uint)(sparseRecords.Length / 4));
        Write(21, (uint)(quantizedDeltas.Length / 2)); Write(22, (uint)spillEntries.Length);
        Write(23, (uint)(_packet.Length / 4)); Write(24, BitConverter.SingleToUInt32Bits(morphWeightThreshold));
        ValidateCanonicalRecords();
    }

    public int BoneCount { get; }
    public int MorphCount { get; }
    public int PacketByteLength => _packet.Length;
    public byte[] CopyPacket() => (byte[])_packet.Clone();

    private void Write(int word, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_packet.AsSpan(word * 4), value);

    private void Append(int word, ReadOnlySpan<byte> bytes, ref int cursor)
    {
        Write(word, (uint)(cursor / 4));
        bytes.CopyTo(_packet.AsSpan(cursor));
        cursor += bytes.Length;
    }
}
