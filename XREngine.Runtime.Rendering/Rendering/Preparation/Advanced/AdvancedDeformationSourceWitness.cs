using XREngine.Data;

namespace XREngine.Rendering;

/// <summary>Exact mesh input identity, independent of material and draw records.</summary>
internal readonly record struct AdvancedDeformationSourceWitness(
    int VertexCount,
    int IndexCount,
    uint PrimitiveType,
    uint MeshletCount,
    AdvancedGpuDeformationInputWitness Input,
    XRMeshSkinningBufferState Skinning,
    ulong CoreIndicesRevision,
    ulong CoreWeightsRevision,
    ulong SpillHeadersRevision,
    ulong SpillEntriesRevision,
    string[] BlendshapeNames,
    XRDataBuffer? BlendshapeCounts,
    XRDataBuffer? BlendshapeIndices,
    XRDataBuffer? BlendshapeDeltas,
    ulong BlendshapeCountsRevision,
    ulong BlendshapeIndicesRevision,
    ulong BlendshapeDeltasRevision)
{
    public static AdvancedDeformationSourceWitness Capture(
        XRMesh mesh, uint meshletCount, EAdvancedDeformationMeshPreparationPolicy policy)
    {
        XRMeshSkinningBufferState skinning = mesh.GetSkinningBufferStateSnapshot();
        bool hasMorphs = XRMeshBlendshapeActiveListReader.TryCreate(mesh, out var morphs);
        return new(mesh.VertexCount, mesh.IndexCount, (uint)mesh.Type, meshletCount,
            AdvancedGpuDeformationInputWitness.Capture(mesh, policy), skinning,
            skinning.CoreIndices?.Revision ?? 0, skinning.CoreWeights?.Revision ?? 0,
            skinning.SpillHeaders?.Revision ?? 0, skinning.SpillEntries?.Revision ?? 0,
            mesh.BlendshapeNames, hasMorphs ? morphs.Counts : null,
            hasMorphs ? morphs.Indices : null, hasMorphs ? morphs.Deltas : null,
            hasMorphs ? morphs.Counts.Revision : 0,
            hasMorphs ? morphs.Indices.Revision : 0,
            hasMorphs ? morphs.Deltas.Revision : 0);
    }

    public bool Matches(in AdvancedDeformationSourceWitness other)
        => GetChanges(in other) == EAdvancedDeformationSourceChange.None;

    public EAdvancedDeformationSourceChange GetChanges(in AdvancedDeformationSourceWitness other)
    {
        EAdvancedDeformationSourceChange changes = EAdvancedDeformationSourceChange.None;
        if (VertexCount != other.VertexCount || IndexCount != other.IndexCount || PrimitiveType != other.PrimitiveType || MeshletCount != other.MeshletCount)
            changes |= EAdvancedDeformationSourceChange.Topology;
        if (Input.Policy != other.Input.Policy)
            changes |= EAdvancedDeformationSourceChange.Policy;
        if (Input.GeometryRevision != other.Input.GeometryRevision)
            changes |= EAdvancedDeformationSourceChange.Geometry;
        if (!ReferenceEquals(Input.Morphs, other.Input.Morphs))
            changes |= EAdvancedDeformationSourceChange.MorphState;
        if (Input.RangesRevision != other.Input.RangesRevision)
            changes |= EAdvancedDeformationSourceChange.MorphRanges;
        if (Input.RecordsRevision != other.Input.RecordsRevision)
            changes |= EAdvancedDeformationSourceChange.MorphRecords;
        if (Input.DeltasRevision != other.Input.DeltasRevision)
            changes |= EAdvancedDeformationSourceChange.MorphDeltas;
        if (Input.MetadataRevision != other.Input.MetadataRevision)
            changes |= EAdvancedDeformationSourceChange.MorphMetadata;
        if (!ReferenceEquals(Skinning, other.Skinning))
            changes |= EAdvancedDeformationSourceChange.SkinningState;
        if (CoreIndicesRevision != other.CoreIndicesRevision)
            changes |= EAdvancedDeformationSourceChange.SkinningIndices;
        if (CoreWeightsRevision != other.CoreWeightsRevision)
            changes |= EAdvancedDeformationSourceChange.SkinningWeights;
        if (SpillHeadersRevision != other.SpillHeadersRevision)
            changes |= EAdvancedDeformationSourceChange.SkinningSpillHeaders;
        if (SpillEntriesRevision != other.SpillEntriesRevision)
            changes |= EAdvancedDeformationSourceChange.SkinningSpillEntries;
        if (!ReferenceEquals(BlendshapeNames, other.BlendshapeNames))
            changes |= EAdvancedDeformationSourceChange.BlendshapeNames;
        if (!ReferenceEquals(BlendshapeCounts, other.BlendshapeCounts) || BlendshapeCountsRevision != other.BlendshapeCountsRevision)
            changes |= EAdvancedDeformationSourceChange.BlendshapeCounts;
        if (!ReferenceEquals(BlendshapeIndices, other.BlendshapeIndices) || BlendshapeIndicesRevision != other.BlendshapeIndicesRevision)
            changes |= EAdvancedDeformationSourceChange.BlendshapeIndices;
        if (!ReferenceEquals(BlendshapeDeltas, other.BlendshapeDeltas) || BlendshapeDeltasRevision != other.BlendshapeDeltasRevision)
            changes |= EAdvancedDeformationSourceChange.BlendshapeDeltas;
        return changes;
    }
}
