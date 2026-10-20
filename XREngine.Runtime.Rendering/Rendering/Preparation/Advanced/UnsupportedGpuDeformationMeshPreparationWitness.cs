namespace XREngine.Rendering;

/// <summary>Exact mesh and skinning inputs that failed CPU preparation.</summary>
internal sealed class UnsupportedGpuDeformationMeshPreparationWitness
{
    internal long GeometryRevision;
    internal uint TopologyGeneration;
    internal EAdvancedDeformationMeshPreparationPolicy Policy;
    internal required XRMeshSkinningBufferState SkinningState;
    internal ulong CoreIndicesRevision;
    internal ulong CoreWeightsRevision;
    internal ulong SpillHeadersRevision;
    internal ulong SpillEntriesRevision;
    internal uint CoreIndicesCount;
    internal uint CoreWeightsCount;
    internal uint SpillHeadersCount;
    internal uint SpillEntriesCount;
    internal bool CoreIndicesReadable;
    internal bool CoreWeightsReadable;
    internal bool SpillHeadersReadable;
    internal bool SpillEntriesReadable;

    /// <summary>
    /// Returns true while the mesh, topology, preparation policy, and skinning
    /// inputs are the same as when preparation failed.
    /// </summary>
    internal bool Matches(XRMesh mesh, uint topologyGeneration,
        EAdvancedDeformationMeshPreparationPolicy policy)
    {
        if (GeometryRevision != mesh.GeometryRevision ||
            TopologyGeneration != topologyGeneration ||
            Policy != policy ||
            !ReferenceEquals(SkinningState, mesh.GetSkinningBufferStateSnapshot()))
            return false;

        return CoreIndicesRevision == (SkinningState.CoreIndices?.Revision ?? 0UL) &&
            CoreWeightsRevision == (SkinningState.CoreWeights?.Revision ?? 0UL) &&
            SpillHeadersRevision == (SkinningState.SpillHeaders?.Revision ?? 0UL) &&
            SpillEntriesRevision == (SkinningState.SpillEntries?.Revision ?? 0UL) &&
            CoreIndicesCount == (SkinningState.CoreIndices?.ElementCount ?? 0u) &&
            CoreWeightsCount == (SkinningState.CoreWeights?.ElementCount ?? 0u) &&
            SpillHeadersCount == (SkinningState.SpillHeaders?.ElementCount ?? 0u) &&
            SpillEntriesCount == (SkinningState.SpillEntries?.ElementCount ?? 0u) &&
            CoreIndicesReadable == IsReadable(SkinningState.CoreIndices) &&
            CoreWeightsReadable == IsReadable(SkinningState.CoreWeights) &&
            SpillHeadersReadable == IsReadable(SkinningState.SpillHeaders) &&
            SpillEntriesReadable == IsReadable(SkinningState.SpillEntries);
    }

    internal static bool IsReadable(XRDataBuffer? buffer)
        => buffer is { IsDestroyed: false, ClientSideSource: not null };
}
