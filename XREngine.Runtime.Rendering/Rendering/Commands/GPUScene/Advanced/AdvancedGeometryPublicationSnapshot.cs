namespace XREngine.Rendering.Commands;

/// <summary>
/// Publication-ring capture of every canonical geometry input stream.  Consumers
/// use these snapshots rather than a mutable scene or legacy GPUScene atlas.
/// </summary>
public sealed class AdvancedGeometryPublicationSnapshot
{
    internal AdvancedGeometryPublicationSnapshot(AdvancedGeometryDatabase geometry)
    {
        _geometry = geometry;
    }

    private readonly AdvancedGeometryDatabase _geometry;

    public AdvancedImmutableByteArenaPublicationSnapshot StaticVertices { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot Indices { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot PreSkinnedCurrent { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot PreSkinnedPrevious { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot MeshletDescriptors { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot MeshletVertexIndices { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot MeshletTriangleWords { get; private set; }

    internal void Capture()
    {
        StaticVertices = _geometry.StaticVertexArena.CapturePublicationSnapshot();
        Indices = _geometry.IndexArena.CapturePublicationSnapshot();
        PreSkinnedCurrent = _geometry.PreSkinnedCurrentArena.CapturePublicationSnapshot();
        PreSkinnedPrevious = _geometry.PreSkinnedPreviousArena.CapturePublicationSnapshot();
        MeshletDescriptors = _geometry.MeshletDescriptorArena.CapturePublicationSnapshot();
        MeshletVertexIndices = _geometry.MeshletVertexIndexArena.CapturePublicationSnapshot();
        MeshletTriangleWords = _geometry.MeshletTriangleWordArena.CapturePublicationSnapshot();
    }
}
