using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>CPU-only cold mesh payload retained until one atomic static append.</summary>
internal sealed class PendingGpuDeformationMeshPreparation
{
    internal const int Index = 0;
    internal const int Count = 1;
    internal const int Pack = 2;
    internal const int Vertices = 3;
    internal const int Spill = 4;
    internal const int Influences = 5;
    internal const int Commit = 6;

    internal required XRMesh Mesh;
    internal required uint TopologyGeneration;
    internal required long GeometryRevision;
    internal required int VertexCount;
    internal required Vertex[] SourceVertices;
    internal required string[] Names;
    internal int ActiveBlendshapeCount;
    internal ulong LastOwnerVisitFrame;
    internal int Stage;
    internal int ShapeIndex;
    internal int VertexIndex;
    internal uint RecordCount;
    internal uint DeltaCount = 1u;
    internal uint PackedRecordCount;
    internal uint PackedDeltaCount = 1u;
    internal int[] SourceIndices = [];
    internal AdvancedDeformedVertex[] VerticesScratch = [];
    internal AdvancedSkinInfluence[] InfluencesScratch = [];
    internal AdvancedSpillInfluence[] SpillScratch = [];
    internal AdvancedBlendshapeRange[] RangesScratch = [];
    internal AdvancedBlendshapeSparseRecord[] RecordsScratch = [];
    internal Vector4[] DeltasScratch = [];
    internal XRMeshSkinningBufferState? SkinningState;
    internal ulong CoreIndicesRevision;
    internal ulong CoreWeightsRevision;
    internal ulong SpillHeadersRevision;
    internal ulong SpillEntriesRevision;
    internal uint SpillCount;
    internal bool Unsupported;
    internal readonly Dictionary<string, int> FirstNameIndices = new(StringComparer.Ordinal);
}
