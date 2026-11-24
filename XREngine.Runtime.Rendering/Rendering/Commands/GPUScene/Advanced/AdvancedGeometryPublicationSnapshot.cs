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
    private uint[] _pageTable = [];

    public AdvancedImmutableByteArenaPublicationSnapshot StaticVertices { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot Indices { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot PreSkinnedCurrent { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot PreSkinnedPrevious { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot MeshletDescriptors { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot MeshletVertexIndices { get; private set; }
    public AdvancedImmutableByteArenaPublicationSnapshot MeshletTriangleWords { get; private set; }

    /// <summary>
    /// The geometry page pool. A backend writes its queued slot images into the pool buffer before
    /// it binds <see cref="PageTable"/>.
    /// </summary>
    public AdvancedGeometryPagePool PagePool => _geometry.PagePool;

    /// <summary>
    /// The queue that takes the geometry page feedback of completed GPU frames. A backend that
    /// binds the page request and touch-bit buffers reads them after the GPU finishes the frame and
    /// adds them here. The publisher drains the queue on its next streaming update.
    /// </summary>
    public Meshlets.MeshletPageFeedbackQueue StreamingFeedback => _geometry.StreamingFeedback;

    /// <summary>
    /// The page table at the seal of this publication: the pool slot of each page, or
    /// <see cref="AdvancedGeometryPagePool.NotResident"/>. The page blocks of the geometries in this
    /// publication index it.
    /// </summary>
    public ReadOnlySpan<uint> PageTable => _pageTable.AsSpan(0, PageTableLength);

    /// <summary>The number of entries in <see cref="PageTable"/>.</summary>
    public int PageTableLength { get; private set; }

    /// <summary>
    /// The page pool version at the seal. Two publications with the same version have the same
    /// page table, so a backend can skip the table upload.
    /// </summary>
    public ulong PageTableVersion { get; private set; }

    internal void Capture()
    {
        StaticVertices = _geometry.StaticVertexArena.CapturePublicationSnapshot();
        Indices = _geometry.IndexArena.CapturePublicationSnapshot();
        PreSkinnedCurrent = _geometry.PreSkinnedCurrentArena.CapturePublicationSnapshot();
        PreSkinnedPrevious = _geometry.PreSkinnedPreviousArena.CapturePublicationSnapshot();
        MeshletDescriptors = _geometry.MeshletDescriptorArena.CapturePublicationSnapshot();
        MeshletVertexIndices = _geometry.MeshletVertexIndexArena.CapturePublicationSnapshot();
        MeshletTriangleWords = _geometry.MeshletTriangleWordArena.CapturePublicationSnapshot();
        PageTableLength = _geometry.PagePool.CapturePageTable(ref _pageTable, out ulong pageTableVersion);
        PageTableVersion = pageTableVersion;
    }
}
