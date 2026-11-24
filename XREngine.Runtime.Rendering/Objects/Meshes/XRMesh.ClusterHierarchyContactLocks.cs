namespace XREngine.Rendering;

public partial class XRMesh
{
    private byte[]? _clusterHierarchyContactLocks;

    /// <summary>
    /// Sets the per-vertex contact locks (1 = locked) that the next cluster hierarchy cook of this
    /// mesh adds to its vertex locks: the vertices that lie on a clothing layer over or under another
    /// skinned mesh of the same model (see <c>SkinnedLayerContactLocks</c>). The model cooker sets them
    /// before it builds the payload. They are cook input only and are not saved with the mesh.
    /// </summary>
    public void SetClusterHierarchyContactLocks(byte[]? locks)
        => _clusterHierarchyContactLocks = locks is not null && locks.Length == VertexCount ? locks : null;

    /// <summary>The contact locks for the next cluster hierarchy cook, or null.</summary>
    public byte[]? GetClusterHierarchyContactLocks()
        => _clusterHierarchyContactLocks is { } locks && locks.Length == VertexCount ? locks : null;
}
