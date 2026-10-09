using System.Numerics;
using XREngine.Data;

namespace XREngine.Rendering;

/// <summary>
/// Static aggregate-deformation inputs associated with one exact canonical
/// scene generation. A generation remains immutable while an output slot pins
/// it, so asynchronous consumers never observe rewritten source data.
/// </summary>
/// <remarks>
/// The managed arrays are the append staging (CPU mirror) of the writable
/// generation. When a pinned generation is superseded, its successor adopts
/// those arrays instead of copying them, and the superseded generation keeps
/// only its GPU buffers, whose client-side copies hold the same rows. A
/// released generation that is selected again restores its mirror from those
/// client-side copies (<see cref="TryRestoreCpuMirror"/>).
/// </remarks>
internal sealed class AdvancedGpuDeformationStaticGeneration
{
    private readonly uint _initialVertices;
    private readonly uint _initialAuxiliary;
    private readonly uint _initialRanges;
    private readonly int _maximumJobs;

    public AdvancedGpuDeformationStaticGeneration(
        uint initialVertices,
        uint initialAuxiliary,
        uint initialRanges,
        int maximumJobs)
    {
        _initialVertices = initialVertices;
        _initialAuxiliary = initialAuxiliary;
        _initialRanges = initialRanges;
        _maximumJobs = maximumJobs;
    }

    public GPUScene? Scene { get; private set; }
    public ulong DatabaseEpoch { get; private set; }
    public ulong TopologyGeneration { get; private set; }
    public ulong LastUse { get; set; }
    public uint PinCount { get; set; }
    public AdvancedGpuDeformationStaticBuffers Buffers = null!;
    public Dictionary<XRMesh, AdvancedGpuDeformationMeshSlice> MeshSlices = null!;
    public Dictionary<ulong, int> PayloadHashHeads = null!;
    public List<AdvancedGpuDeformationMeshPayloadEntry> PayloadEntries = null!;
    public AdvancedDeformedVertex[] SourceVertices = null!;
    public AdvancedSkinInfluence[] SkinInfluences = null!;
    public AdvancedSpillInfluence[] SpillInfluences = null!;
    public AdvancedBlendshapeRange[] BlendshapeRanges = null!;
    public AdvancedBlendshapeSparseRecord[] BlendshapeRecords = null!;
    public Vector4[] BlendshapeDeltas = null!;
    public uint SourceVertexCount;
    public uint SkinInfluenceCount;
    public uint SpillInfluenceCount;
    public uint BlendshapeRangeCount;
    public uint BlendshapeRecordCount;
    public uint BlendshapeDeltaCount;
    public uint UploadedSourceVertexCount;
    public uint UploadedSkinInfluenceCount;
    public uint UploadedSpillInfluenceCount;
    public uint UploadedBlendshapeRangeCount;
    public uint UploadedBlendshapeRecordCount;
    public uint UploadedBlendshapeDeltaCount;

    /// <summary>
    /// True while the managed arrays hold this generation's rows. A
    /// generation whose arrays were adopted by a successor has none.
    /// </summary>
    public bool HasCpuMirror { get; private set; }

    public bool Matches(GPUScene scene, ulong databaseEpoch, ulong topologyGeneration)
        => ReferenceEquals(Scene, scene) &&
           DatabaseEpoch == databaseEpoch &&
           TopologyGeneration == topologyGeneration;

    /// <summary>
    /// Rebinds this generation to a new canonical scene revision with no rows.
    /// Pass <paramref name="allocateCpuMirror"/> false when the caller adopts
    /// a predecessor's arrays right after (<see cref="AdoptCpuMirror"/>).
    /// </summary>
    public void Assign(
        GPUScene scene,
        ulong databaseEpoch,
        ulong topologyGeneration,
        bool allocateCpuMirror = true)
    {
        EnsureInitialized(allocateCpuMirror);
        Scene = scene;
        DatabaseEpoch = databaseEpoch;
        TopologyGeneration = topologyGeneration;
        MeshSlices.Clear();
        PayloadHashHeads.Clear();
        PayloadEntries.Clear();
        SourceVertexCount = 0u;
        SkinInfluenceCount = 0u;
        SpillInfluenceCount = 0u;
        BlendshapeRangeCount = 0u;
        BlendshapeRecordCount = 0u;
        BlendshapeDeltaCount = 1u;
        if (HasCpuMirror)
            BlendshapeDeltas[0] = Vector4.Zero;
        UploadedSourceVertexCount = 0u;
        UploadedSkinInfluenceCount = 0u;
        UploadedSpillInfluenceCount = 0u;
        UploadedBlendshapeRangeCount = 0u;
        UploadedBlendshapeRecordCount = 0u;
        UploadedBlendshapeDeltaCount = 0u;
    }

    /// <summary>
    /// Takes <paramref name="source"/>'s managed arrays as this generation's
    /// CPU mirror and releases them from the source. The source keeps its GPU
    /// buffers and counts, which are all a pinned generation needs.
    /// </summary>
    public void AdoptCpuMirror(AdvancedGpuDeformationStaticGeneration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasCpuMirror)
            throw new InvalidOperationException(
                "A deformation generation can only adopt a present CPU mirror.");

        SourceVertices = source.SourceVertices;
        SkinInfluences = source.SkinInfluences;
        SpillInfluences = source.SpillInfluences;
        BlendshapeRanges = source.BlendshapeRanges;
        BlendshapeRecords = source.BlendshapeRecords;
        BlendshapeDeltas = source.BlendshapeDeltas;
        PayloadHashHeads.Clear();
        foreach ((ulong hash, int head) in source.PayloadHashHeads)
            PayloadHashHeads.Add(hash, head);
        PayloadEntries.Clear();
        PayloadEntries.AddRange(source.PayloadEntries);
        HasCpuMirror = true;
        source.ReleaseCpuMirror();
    }

    /// <summary>
    /// Rebuilds the managed arrays of a released generation from its GPU
    /// buffers' client-side copies. Succeeds only when every row was uploaded
    /// and the copies cover them; otherwise the generation cannot be resumed
    /// and must be reassigned.
    /// </summary>
    public bool TryRestoreCpuMirror()
    {
        if (HasCpuMirror)
            return true;
        if (Buffers is null ||
            UploadedSourceVertexCount != SourceVertexCount ||
            UploadedSkinInfluenceCount != SkinInfluenceCount ||
            UploadedSpillInfluenceCount != SpillInfluenceCount ||
            UploadedBlendshapeRangeCount != BlendshapeRangeCount ||
            UploadedBlendshapeRecordCount != BlendshapeRecordCount ||
            UploadedBlendshapeDeltaCount != BlendshapeDeltaCount)
            return false;

        if (!TryRestore(Buffers.SourceVertices, SourceVertexCount, _initialVertices, out AdvancedDeformedVertex[] vertices) ||
            !TryRestore(Buffers.SkinInfluences, SkinInfluenceCount, _initialVertices, out AdvancedSkinInfluence[] influences) ||
            !TryRestore(Buffers.SpillInfluences, SpillInfluenceCount, _initialAuxiliary, out AdvancedSpillInfluence[] spill) ||
            !TryRestore(Buffers.BlendshapeRanges, BlendshapeRangeCount, _initialRanges, out AdvancedBlendshapeRange[] ranges) ||
            !TryRestore(Buffers.BlendshapeRecords, BlendshapeRecordCount, _initialVertices, out AdvancedBlendshapeSparseRecord[] records) ||
            !TryRestore(Buffers.BlendshapeDeltas, BlendshapeDeltaCount, _initialVertices, out Vector4[] deltas))
            return false;

        SourceVertices = vertices;
        SkinInfluences = influences;
        SpillInfluences = spill;
        BlendshapeRanges = ranges;
        BlendshapeRecords = records;
        BlendshapeDeltas = deltas;
        HasCpuMirror = true;
        return true;
    }

    private static bool TryRestore<T>(
        XRDataBuffer<T> buffer,
        uint count,
        uint minimumCapacity,
        out T[] restored) where T : unmanaged
    {
        Span<T> mirror = buffer.GetCpuMirrorSpan();
        if ((uint)mirror.Length < count)
        {
            restored = [];
            return false;
        }
        restored = new T[Math.Max(minimumCapacity, count)];
        mirror[..checked((int)count)].CopyTo(restored);
        return true;
    }

    private void ReleaseCpuMirror()
    {
        SourceVertices = [];
        SkinInfluences = [];
        SpillInfluences = [];
        BlendshapeRanges = [];
        BlendshapeRecords = [];
        BlendshapeDeltas = [];
        HasCpuMirror = false;
    }

    public void EnsureInitialized(bool allocateCpuMirror = true)
    {
        if (allocateCpuMirror && !HasCpuMirror)
        {
            SourceVertices = new AdvancedDeformedVertex[_initialVertices];
            SkinInfluences = new AdvancedSkinInfluence[_initialVertices];
            SpillInfluences = new AdvancedSpillInfluence[_initialAuxiliary];
            BlendshapeRanges = new AdvancedBlendshapeRange[_initialRanges];
            BlendshapeRecords = new AdvancedBlendshapeSparseRecord[_initialVertices];
            BlendshapeDeltas = new Vector4[_initialVertices];
            BlendshapeDeltas[0] = Vector4.Zero;
            HasCpuMirror = true;
        }
        if (Buffers is not null)
            return;

        Buffers = new AdvancedGpuDeformationStaticBuffers(
            _initialVertices,
            _initialVertices,
            _initialAuxiliary,
            _initialRanges,
            _initialVertices,
            _initialVertices);
        MeshSlices = new Dictionary<XRMesh, AdvancedGpuDeformationMeshSlice>(
            _maximumJobs,
            ReferenceEqualityComparer.Instance);
        PayloadHashHeads = new Dictionary<ulong, int>(_maximumJobs);
        PayloadEntries = new List<AdvancedGpuDeformationMeshPayloadEntry>(_maximumJobs);
        BlendshapeDeltaCount = 1u;
    }

    /// <summary>
    /// Frees this generation's buffers, CPU mirror and mesh references. Used for a
    /// generation whose scene was destroyed: it can never match again, and
    /// otherwise keeps its high-water capacity and the scene's meshes until it is
    /// reassigned. The next assignment allocates at the initial capacity.
    /// </summary>
    public void ReleaseStorage()
    {
        if (PinCount != 0u)
            throw new InvalidOperationException(
                "A pinned deformation generation cannot release its storage.");

        Buffers?.Destroy();
        Buffers = null!;
        MeshSlices = null!;
        PayloadHashHeads = null!;
        PayloadEntries = null!;
        ReleaseCpuMirror();
        Scene = null;
        DatabaseEpoch = 0UL;
        TopologyGeneration = 0UL;
        SourceVertexCount = 0u;
        SkinInfluenceCount = 0u;
        SpillInfluenceCount = 0u;
        BlendshapeRangeCount = 0u;
        BlendshapeRecordCount = 0u;
        BlendshapeDeltaCount = 0u;
        UploadedSourceVertexCount = 0u;
        UploadedSkinInfluenceCount = 0u;
        UploadedSpillInfluenceCount = 0u;
        UploadedBlendshapeRangeCount = 0u;
        UploadedBlendshapeRecordCount = 0u;
        UploadedBlendshapeDeltaCount = 0u;
    }

    public void ClearMeshSlices()
    {
        if (MeshSlices is not null)
            MeshSlices.Clear();
        PayloadHashHeads?.Clear();
        PayloadEntries?.Clear();
    }

    public void Destroy()
    {
        if (Buffers is not null)
            Buffers.Destroy();
    }
}
