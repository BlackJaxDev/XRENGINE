using System.Numerics;
using XREngine.Data;

namespace XREngine.Rendering;

/// <summary>
/// Static aggregate-deformation inputs associated with one exact canonical
/// scene generation. A generation remains immutable while an output slot pins
/// it, so asynchronous consumers never observe rewritten source data.
/// </summary>
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

    public bool Matches(GPUScene scene, ulong databaseEpoch, ulong topologyGeneration)
        => ReferenceEquals(Scene, scene) &&
           DatabaseEpoch == databaseEpoch &&
           TopologyGeneration == topologyGeneration;

    public void Assign(GPUScene scene, ulong databaseEpoch, ulong topologyGeneration)
    {
        EnsureInitialized();
        Scene = scene;
        DatabaseEpoch = databaseEpoch;
        TopologyGeneration = topologyGeneration;
        MeshSlices.Clear();
        SourceVertexCount = 0u;
        SkinInfluenceCount = 0u;
        SpillInfluenceCount = 0u;
        BlendshapeRangeCount = 0u;
        BlendshapeRecordCount = 0u;
        BlendshapeDeltaCount = 1u;
        BlendshapeDeltas[0] = Vector4.Zero;
        UploadedSourceVertexCount = 0u;
        UploadedSkinInfluenceCount = 0u;
        UploadedSpillInfluenceCount = 0u;
        UploadedBlendshapeRangeCount = 0u;
        UploadedBlendshapeRecordCount = 0u;
        UploadedBlendshapeDeltaCount = 0u;
    }

    public void EnsureInitialized()
    {
        if (Buffers is not null)
            return;

        SourceVertices = new AdvancedDeformedVertex[_initialVertices];
        SkinInfluences = new AdvancedSkinInfluence[_initialVertices];
        SpillInfluences = new AdvancedSpillInfluence[_initialAuxiliary];
        BlendshapeRanges = new AdvancedBlendshapeRange[_initialRanges];
        BlendshapeRecords = new AdvancedBlendshapeSparseRecord[_initialVertices];
        BlendshapeDeltas = new Vector4[_initialVertices];
        BlendshapeDeltas[0] = Vector4.Zero;
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
        BlendshapeDeltaCount = 1u;
    }

    public void ClearMeshSlices()
    {
        if (MeshSlices is not null)
            MeshSlices.Clear();
    }

    public void Destroy()
    {
        if (Buffers is not null)
            Buffers.Destroy();
    }
}
