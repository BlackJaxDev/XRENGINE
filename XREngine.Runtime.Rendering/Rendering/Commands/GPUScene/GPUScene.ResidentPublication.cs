namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private AdvancedGpuScenePublisher _advancedScenePublisher = new();
    private bool _advancedScenePublisherDisposed;
    private AdvancedGlobalResourceCapture _advancedGlobalResources;
    // A capture is consumed by precisely one requested publication. Retaining the
    // capture itself lets the completed publication's later frame package inspect
    // its global rows, while this bit prevents a later standalone swap from
    // accidentally reusing an older world-swap capture.
    private bool _hasAdvancedGlobalResourceCapture;
    private int _advancedPublicationRequested;

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

    public bool AdvancedPublicationFaulted
        => _advancedScenePublisher.PublicationFaulted;

    public EAdvancedGpuScenePublicationFault AdvancedPublicationFault
        => AdvancedSharedDatabase.PublicationFault;

    public ReadOnlySpan<LegacyCanonicalDrawMapping> LegacyCanonicalDrawMappings
        => _advancedScenePublisher.LegacyMappings;

    public ReadOnlySpan<AdvancedGpuDirtyOwnerRange> AdvancedDirtyOwnerRanges
        => _advancedScenePublisher.DirtyOwnerRanges;

    public AdvancedGlobalResourceCapture AdvancedGlobalResources
        => _advancedGlobalResources;

    /// <summary>
    /// Gets whether a pipeline that consumes the canonical resident scene has
    /// requested a publication for the pending swap boundary.
    /// </summary>
    internal bool AdvancedPublicationRequested
        => System.Threading.Volatile.Read(ref _advancedPublicationRequested) != 0;

    /// <summary>
    /// Requests canonical resident-scene publication at the next swap boundary.
    /// Multiple viewports coalesce into one allocation-free request bit.
    /// </summary>
    internal void RequestAdvancedResidentPublication()
        => System.Threading.Interlocked.Exchange(ref _advancedPublicationRequested, 1);

    public void SetAdvancedGlobalResources(in AdvancedGlobalResourceCapture capture)
    {
        _advancedGlobalResources = capture;
        _hasAdvancedGlobalResourceCapture = true;
    }

    /// <summary>
    /// Supplies global rows captured for an explicit output identity. The scene
    /// publication must use the same identity; accepting a mismatched capture
    /// would produce a package whose scene and global-resource generations disagree.
    /// </summary>
    public void SetAdvancedGlobalResources(
        ulong canonicalFrameId,
        in AdvancedGlobalResourceCapture capture)
    {
        if (canonicalFrameId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(canonicalFrameId), "An explicit canonical frame ID must be nonzero.");
        if (capture.FrameId != canonicalFrameId)
        {
            throw new ArgumentException(
                "The advanced global-resource capture does not belong to the requested canonical frame.",
                nameof(capture));
        }

        SetAdvancedGlobalResources(in capture);
    }

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

    public bool TryGetCanonicalCompatibilityReason(
        IRenderCommandMesh source,
        int primitiveIndex,
        out EAdvancedCanonicalCompatibilityReason reason)
        => _advancedScenePublisher.TryGetCanonicalCompatibilityReason(
            source,
            primitiveIndex,
            out reason);

    public bool WasCanonicalDrawAddedThisPublication(AdvancedGpuHandle draw)
        => _advancedScenePublisher.WasDrawAddedThisPublication(draw);

    private void PublishAdvancedResidentSceneIfRequested()
    {
        if (System.Threading.Interlocked.Exchange(ref _advancedPublicationRequested, 0) == 0)
            return;

        AdvancedGlobalResourceCapture globalResources = _advancedGlobalResources;
        ulong frameId;
        if (_hasAdvancedGlobalResourceCapture)
        {
            _hasAdvancedGlobalResourceCapture = false;
            frameId = globalResources.FrameId;
        }
        else
        {
            // GPUScene can be swapped without RuntimeWorldRenderer. That legacy
            // standalone path has no world capture, so it retains ambient identity
            // and an explicitly empty global-resource cohort.
            frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            globalResources = AdvancedGlobalResourceCapture.Empty(frameId);
        }

        _advancedScenePublisher.Publish(
            this,
            frameId,
            in globalResources);
    }
}
