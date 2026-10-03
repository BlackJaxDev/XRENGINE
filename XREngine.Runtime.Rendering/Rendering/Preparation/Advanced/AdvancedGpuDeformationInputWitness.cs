namespace XREngine.Rendering;

/// <summary>Exact source identity retained with decoded immutable aggregate mesh inputs.</summary>
internal readonly record struct AdvancedGpuDeformationInputWitness(
    EAdvancedDeformationMeshPreparationPolicy Policy,
    long GeometryRevision,
    XRMeshBlendshapeBufferState? Morphs,
    ulong RangesRevision,
    ulong RecordsRevision,
    ulong DeltasRevision,
    ulong MetadataRevision)
{
    internal static AdvancedGpuDeformationInputWitness Capture(
        XRMesh mesh,
        EAdvancedDeformationMeshPreparationPolicy policy)
    {
        if (policy == EAdvancedDeformationMeshPreparationPolicy.AuthoredVertices)
            return new(policy, mesh.GeometryRevision, null, 0, 0, 0, 0);
        XRMeshBlendshapeBufferState morphs = mesh.GetBlendshapeBufferStateSnapshot();
        return new(policy, mesh.GeometryRevision, morphs, morphs.SparseShapeRanges?.Revision ?? 0,
            morphs.SparseRecords?.Revision ?? 0, morphs.QuantizedDeltas?.Revision ?? 0,
            morphs.QuantizationMetadata?.Revision ?? 0);
    }

    internal bool Matches(XRMesh mesh, EAdvancedDeformationMeshPreparationPolicy policy)
        => Policy == policy && GeometryRevision == mesh.GeometryRevision &&
           (policy == EAdvancedDeformationMeshPreparationPolicy.AuthoredVertices ||
            Morphs is { } morphs && ReferenceEquals(morphs, mesh.GetBlendshapeBufferStateSnapshot()) &&
            morphs.SparseShapeRanges is not { IsDestroyed: true } && morphs.SparseRecords is not { IsDestroyed: true } &&
            morphs.QuantizedDeltas is not { IsDestroyed: true } && morphs.QuantizationMetadata is not { IsDestroyed: true } &&
            RangesRevision == (morphs.SparseShapeRanges?.Revision ?? 0) && RecordsRevision == (morphs.SparseRecords?.Revision ?? 0) &&
            DeltasRevision == (morphs.QuantizedDeltas?.Revision ?? 0) && MetadataRevision == (morphs.QuantizationMetadata?.Revision ?? 0));
}
