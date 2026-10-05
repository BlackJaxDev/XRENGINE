using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Decodes per-vertex bone influences directly from a mesh's packed Core4
/// skinning buffers: four core (bone + 1, unorm8 weight) slots per vertex plus an
/// optional spill table for vertices with more influences. Bone indices address
/// <see cref="XRMesh.UtilizedBones"/>. This replaces reading per-vertex weight
/// dictionaries; it reads the client-side buffer copies and allocates nothing.
/// </summary>
public readonly struct XRMeshSkinningInfluenceReader
{
    private readonly XRDataBuffer _coreIndices;
    private readonly XRDataBuffer _coreWeights;
    private readonly XRDataBuffer? _spillHeaders;
    private readonly XRDataBuffer? _spillEntries;
    private readonly bool _wideIndices;
    private readonly int _vertexCount;

    private XRMeshSkinningInfluenceReader(
        XRDataBuffer coreIndices,
        XRDataBuffer coreWeights,
        XRDataBuffer? spillHeaders,
        XRDataBuffer? spillEntries,
        bool wideIndices,
        int vertexCount,
        int boneCount,
        int maxSpillInfluenceCount)
    {
        _coreIndices = coreIndices;
        _coreWeights = coreWeights;
        _spillHeaders = spillHeaders;
        _spillEntries = spillEntries;
        _wideIndices = wideIndices;
        _vertexCount = vertexCount;
        BoneCount = boneCount;
        MaxInfluenceCount = 4 + Math.Max(0, maxSpillInfluenceCount);
    }

    /// <summary>Number of palette bones the decoded indices address.</summary>
    public int BoneCount { get; }

    /// <summary>Largest number of influences one vertex can decode to.</summary>
    public int MaxInfluenceCount { get; }

    public bool IsValid => _coreIndices is not null;

    /// <summary>
    /// Creates a reader over the mesh's current canonical skinning buffers.
    /// Returns false when the mesh is not skinned or the buffers have no
    /// client-side data.
    /// </summary>
    public static bool TryCreate(XRMesh mesh, out XRMeshSkinningInfluenceReader reader)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        reader = default;
        XRMeshSkinningBufferState state = mesh.GetSkinningBufferStateSnapshot();
        int vertexCount = mesh.VertexCount;
        // Bone rebinds replace UtilizedBones without repacking, so decoded
        // indices address the mesh's current bone table.
        if (vertexCount <= 0 ||
            mesh.UtilizedBones is not { Length: > 0 } bones ||
            state.CoreIndexFormat is not (SkinningCoreIndexFormat.Core4x8 or SkinningCoreIndexFormat.Core4x16) ||
            state.CoreIndices is not { ClientSideSource: not null } coreIndices ||
            state.CoreWeights is not { ClientSideSource: not null } coreWeights ||
            coreIndices.ElementCount < (uint)vertexCount ||
            coreWeights.ElementCount < (uint)vertexCount)
        {
            return false;
        }

        XRDataBuffer? spillHeaders = null;
        XRDataBuffer? spillEntries = null;
        if (state.HasSpillInfluences)
        {
            if (state.SpillHeaders is not { ClientSideSource: not null } headers ||
                state.SpillEntries is not { ClientSideSource: not null } entries ||
                headers.ElementCount < (uint)vertexCount)
            {
                return false;
            }
            spillHeaders = headers;
            spillEntries = entries;
        }

        reader = new XRMeshSkinningInfluenceReader(
            coreIndices,
            coreWeights,
            spillHeaders,
            spillEntries,
            state.CoreIndexFormat == SkinningCoreIndexFormat.Core4x16,
            vertexCount,
            bones.Length,
            state.HasSpillInfluences ? state.MaxSpillInfluenceCount : 0);
        return true;
    }

    /// <summary>
    /// Writes the vertex's influences as palette bone indices and normalized
    /// weights, strongest first as packed, and returns how many were written.
    /// Slots beyond the destination length are dropped.
    /// </summary>
    public unsafe int ReadInfluences(int vertexIndex, Span<int> boneIndices, Span<float> weights)
    {
        if ((uint)vertexIndex >= (uint)_vertexCount)
            throw new ArgumentOutOfRangeException(nameof(vertexIndex));

        int capacity = Math.Min(boneIndices.Length, weights.Length);
        int written = 0;
        byte* weightBytes = (byte*)_coreWeights.Address.Pointer + vertexIndex * _coreWeights.ElementSize;
        byte* indexBytes = (byte*)_coreIndices.Address.Pointer + vertexIndex * _coreIndices.ElementSize;
        for (int slot = 0; slot < 4 && written < capacity; slot++)
        {
            uint boneIndexPlusOne = _wideIndices ? ((ushort*)indexBytes)[slot] : indexBytes[slot];
            byte weight = weightBytes[slot];
            if (TryDecode(boneIndexPlusOne, weight, out int bone, out float value))
            {
                boneIndices[written] = bone;
                weights[written] = value;
                written++;
            }
        }

        if (_spillHeaders is null || _spillEntries is null)
            return written;

        uint header = ((uint*)_spillHeaders.Address.Pointer)[vertexIndex];
        uint spillOffset = header & 0x00FF_FFFFu;
        uint spillCount = header >> 24;
        uint entryCount = _spillEntries.ElementCount;
        uint* entries = (uint*)_spillEntries.Address.Pointer;
        for (uint spill = 0u; spill < spillCount && written < capacity; spill++)
        {
            uint entryIndex = spillOffset + spill;
            if (entryIndex >= entryCount)
                break;
            uint packed = entries[entryIndex];
            if (TryDecode(packed & 0xFFFFu, (byte)((packed >> 16) & 0xFFu), out int bone, out float value))
            {
                boneIndices[written] = bone;
                weights[written] = value;
                written++;
            }
        }
        return written;
    }

    private bool TryDecode(uint boneIndexPlusOne, byte quantizedWeight, out int bone, out float weight)
    {
        bone = -1;
        weight = 0.0f;
        if (boneIndexPlusOne == 0u || quantizedWeight == 0 || boneIndexPlusOne > (uint)BoneCount)
            return false;
        bone = (int)(boneIndexPlusOne - 1u);
        weight = quantizedWeight / (float)byte.MaxValue;
        return true;
    }
}
