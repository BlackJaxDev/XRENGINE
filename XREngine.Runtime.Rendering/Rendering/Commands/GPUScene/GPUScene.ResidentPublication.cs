namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private readonly AdvancedGpuScenePublisher _advancedScenePublisher = new();

    /// <summary>
    /// Canonical renderer-neutral scene authority dual-published with the legacy
    /// GPU scene at the frame swap boundary.
    /// </summary>
    public AdvancedSharedGpuSceneDatabase AdvancedSharedDatabase
        => _advancedScenePublisher.Database;

    public AdvancedGpuScenePublicationReference AdvancedScenePublication
        => _advancedScenePublisher.CurrentPublication;

    public int AdvancedTopologyDeltaCount
        => _advancedScenePublisher.TopologyDeltaCount;

    public int AdvancedContentDeltaCount
        => _advancedScenePublisher.ContentDeltaCount;

    public bool AdvancedPublicationRejected
        => _advancedScenePublisher.PublicationRejected;

    public ReadOnlySpan<LegacyCanonicalDrawMapping> LegacyCanonicalDrawMappings
        => _advancedScenePublisher.LegacyMappings;

    public ReadOnlySpan<AdvancedGpuDirtyOwnerRange> AdvancedDirtyOwnerRanges
        => _advancedScenePublisher.DirtyOwnerRanges;

    public bool TryGetCanonicalAdvancedPreparationHandles(
        uint commandIndex,
        out AdvancedGpuHandle draw,
        out AdvancedGpuHandle geometry,
        out AdvancedGpuHandle material)
        => _advancedScenePublisher.TryGetCanonicalHandles(
            commandIndex,
            out draw,
            out geometry,
            out material);

    public bool TryGetCanonicalAdvancedPreparationHandles(
        uint commandIndex,
        out AdvancedGpuHandle draw,
        out AdvancedGpuHandle geometry,
        out AdvancedGpuHandle material,
        out AdvancedGpuHandle deformation)
        => _advancedScenePublisher.TryGetCanonicalHandles(
            commandIndex,
            out draw,
            out geometry,
            out material,
            out deformation);

    public bool TryGetCanonicalDraw(
        IRenderCommandMesh source,
        out AdvancedGpuHandle draw)
        => _advancedScenePublisher.TryGetCanonicalDraw(source, out draw);

    public bool TryGetCanonicalDraw(
        IRenderCommandMesh source,
        int primitiveIndex,
        out AdvancedGpuHandle draw)
        => _advancedScenePublisher.TryGetCanonicalDraw(
            source,
            primitiveIndex,
            out draw);

    public bool WasCanonicalDrawAddedThisPublication(AdvancedGpuHandle draw)
        => _advancedScenePublisher.WasDrawAddedThisPublication(draw);

    private void PublishAdvancedResidentScene()
        => _advancedScenePublisher.Publish(
            this,
            RuntimeEngine.Rendering.State.RenderFrameId);
}
