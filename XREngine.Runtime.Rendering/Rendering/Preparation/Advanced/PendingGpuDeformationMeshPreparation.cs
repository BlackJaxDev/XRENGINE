using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// CPU-only cold mesh payload retained until one atomic static append. Every
/// input is read from the mesh's packed attribute, skinning and blendshape
/// buffers; the witnesses below detect a buffer replaced mid-preparation.
/// </summary>
internal sealed class PendingGpuDeformationMeshPreparation
{
    internal const int CanonicalMorphCount = -2;
    internal const int CanonicalMorphPack = -1;
    internal const int Count = 0;
    internal const int Pack = 1;
    internal const int Vertices = 2;
    internal const int Spill = 3;
    internal const int Influences = 4;
    internal const int Commit = 5;

    internal required XRMesh Mesh;
    internal required uint TopologyGeneration;
    internal required long GeometryRevision;
    internal required AdvancedGpuDeformationInputWitness InputWitness;
    internal required int VertexCount;
    internal required string[] Names;
    internal int ActiveBlendshapeCount;
    /// <summary>Active-list reader of the blendshape buffers the payload was built from.</summary>
    internal XRMeshBlendshapeActiveListReader Blendshapes;
    internal ulong BlendshapeCountsRevision;
    internal ulong BlendshapeIndicesRevision;
    internal ulong BlendshapeDeltasRevision;
    internal ulong LastOwnerVisitFrame;
    internal int Stage;
    internal int ShapeIndex;
    internal int VertexIndex;
    internal uint RecordCount;
    internal uint DeltaCount = 1u;
    internal uint PackedRecordCount;
    internal uint PackedDeltaCount = 1u;
    internal AdvancedDeformedVertex[] VerticesScratch = [];
    internal AdvancedSkinInfluence[] InfluencesScratch = [];
    internal AdvancedSpillInfluence[] SpillScratch = [];
    internal AdvancedBlendshapeRange[] RangesScratch = [];
    internal AdvancedBlendshapeSparseRecord[] RecordsScratch = [];
    internal Vector4[] DeltasScratch = [];
    internal XRMeshSkinningBufferState? SkinningState;
    internal XRMeshBlendshapeBufferState? CanonicalMorphs;
    internal ulong MorphRangesRevision;
    internal ulong MorphRecordsRevision;
    internal ulong MorphDeltasRevision;
    internal ulong MorphMetadataRevision;
    internal ulong CoreIndicesRevision;
    internal ulong CoreWeightsRevision;
    internal ulong SpillHeadersRevision;
    internal ulong SpillEntriesRevision;
    internal uint SpillCount;
    internal bool Unsupported;
}
