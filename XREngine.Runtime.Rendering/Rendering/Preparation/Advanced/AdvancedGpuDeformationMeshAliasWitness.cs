using XREngine.Data;

namespace XREngine.Rendering;

/// <summary>Checks that a mesh alias still names its packed source data.</summary>
internal sealed class AdvancedGpuDeformationMeshAliasWitness
{
    public required AdvancedGpuDeformationMeshSlice Slice { get; init; }
    public required long GeometryRevision { get; init; }
    public required XRMeshSkinningBufferState SkinningState { get; init; }
    public required ulong CoreIndicesRevision { get; init; }
    public required ulong CoreWeightsRevision { get; init; }
    public required ulong SpillHeadersRevision { get; init; }
    public required ulong SpillEntriesRevision { get; init; }
    public required string[] BlendshapeNames { get; init; }
    public required XRDataBuffer? BlendshapeCounts { get; init; }
    public required XRDataBuffer? BlendshapeIndices { get; init; }
    public required XRDataBuffer? BlendshapeDeltas { get; init; }
    public required ulong BlendshapeCountsRevision { get; init; }
    public required ulong BlendshapeIndicesRevision { get; init; }
    public required ulong BlendshapeDeltasRevision { get; init; }

    public bool Matches(XRMesh mesh, in AdvancedGpuDeformationMeshSlice slice)
    {
        if (Slice != slice || GeometryRevision != mesh.GeometryRevision ||
            !ReferenceEquals(SkinningState, mesh.GetSkinningBufferStateSnapshot()) ||
            CoreIndicesRevision != (SkinningState.CoreIndices?.Revision ?? 0UL) ||
            CoreWeightsRevision != (SkinningState.CoreWeights?.Revision ?? 0UL) ||
            SpillHeadersRevision != (SkinningState.SpillHeaders?.Revision ?? 0UL) ||
            SpillEntriesRevision != (SkinningState.SpillEntries?.Revision ?? 0UL) ||
            !ReferenceEquals(BlendshapeNames, mesh.BlendshapeNames))
            return false;

        bool hasBlendshapes = XRMeshBlendshapeActiveListReader.TryCreate(mesh,
            out XRMeshBlendshapeActiveListReader blendshapes);
        return ReferenceEquals(BlendshapeCounts,
                   hasBlendshapes ? blendshapes.Counts : null) &&
               ReferenceEquals(BlendshapeIndices,
                   hasBlendshapes ? blendshapes.Indices : null) &&
               ReferenceEquals(BlendshapeDeltas,
                   hasBlendshapes ? blendshapes.Deltas : null) &&
               BlendshapeCountsRevision ==
                   (hasBlendshapes ? blendshapes.Counts.Revision : 0UL) &&
               BlendshapeIndicesRevision ==
                   (hasBlendshapes ? blendshapes.Indices.Revision : 0UL) &&
               BlendshapeDeltasRevision ==
                   (hasBlendshapes ? blendshapes.Deltas.Revision : 0UL);
    }
}
