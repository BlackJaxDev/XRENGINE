namespace XREngine.Rendering;

/// <summary>
/// A ready mesh slice that the mesh preparation pre-pass of one extraction produced for one
/// command, with the state that it depends on. Extraction reuses the slice instead of a second
/// <see cref="AdvancedGpuDeformationResources.TryPrepareMesh"/> call while that state is unchanged.
/// </summary>
/// <param name="Slice">The ready slice.</param>
/// <param name="SliceRevision">The value of <see cref="AdvancedGpuDeformationResources.MeshSliceRevision"/> after the preparation.</param>
/// <param name="GeometryRevision">The mesh geometry revision at the preparation.</param>
/// <param name="Ready">False when the pre-pass did not produce a ready slice for the command.</param>
internal readonly record struct AdvancedPreparedDeformationMesh(
    AdvancedGpuDeformationMeshSlice Slice,
    ulong SliceRevision,
    long GeometryRevision,
    bool Ready)
{
    /// <summary>
    /// True when <see cref="Slice"/> still names the source rows of <paramref name="mesh"/>: no
    /// mesh slice changed since the preparation, and the mesh keeps its geometry revision, its
    /// vertex count, and its topology generation.
    /// </summary>
    public bool IsReusable(ulong sliceRevision, XRMesh mesh, uint topologyGeneration)
        => Ready &&
            SliceRevision == sliceRevision &&
            GeometryRevision == mesh.GeometryRevision &&
            Slice.TopologyGeneration == topologyGeneration &&
            Slice.VertexCount == (uint)mesh.VertexCount;
}
