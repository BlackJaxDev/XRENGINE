using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>One completion-retained canonical publication and its physical WebGPU arena images.</summary>
internal sealed class WebGpuAdvancedSceneSlot : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly WebGpuAdvancedSceneArena _sceneImage = new();
    private readonly WebGpuAdvancedGeometryArena _geometryImage = new();
    private AdvancedGpuScenePublicationLease _lease;
    private AdvancedGpuScenePublicationSnapshot? _snapshot;
    private AdvancedGpuScenePublication _residentScene;
    private bool _residentGeometry;
    private bool _stagedScene;
    private bool _stagedGeometry;

    internal WebGpuAdvancedSceneSlot(WebGpuRendererHost renderer, int index)
    {
        _renderer = renderer;
        SlotIndex = index;
        SceneArena = new(renderer, $"Canonical scene slot {index}");
        GeometryArena = new(renderer, $"Canonical geometry slot {index}");
    }

    internal int SlotIndex { get; }
    internal WebGpuOwnedStorageBuffer SceneArena { get; }
    internal WebGpuOwnedStorageBuffer GeometryArena { get; }
    internal int SceneBuffer => SceneArena.ResourceHandle;
    internal int GeometryBuffer => GeometryArena.ResourceHandle;
    internal AdvancedGpuScenePublicationSnapshot Snapshot => _snapshot
        ?? throw new InvalidOperationException("WebGPU.Advanced.PublicationReleased: the slot no longer retains its canonical publication.");
    internal AdvancedGpuScenePublication Publication => _lease.Reference.Publication;
    internal uint RecordingSequence { get; private set; }
    internal uint SubmittedSequence { get; private set; }
    internal bool IsAvailable => !_lease.IsValid && SubmittedSequence == 0 && RecordingSequence == 0;
    internal uint CurrentDeformationBytes { get; private set; }
    internal uint PreviousDeformationBytes { get; private set; }
    internal AdvancedGpuDeformationPublication CopiedDeformation { get; set; }

    internal void Prepare(AdvancedGpuScenePublicationLease lease,
        AdvancedGpuScenePublicationSnapshot snapshot, uint frameSequence, uint currentDeformationBytes, uint previousDeformationBytes)
    {
        if (!IsAvailable || !lease.IsValid || frameSequence == 0)
            throw new InvalidOperationException("WebGPU.Advanced.SlotOwned: a recorded or GPU-owned slot cannot be overwritten.");
        AdvancedGpuScenePublication publication = lease.Reference.Publication;
        bool sceneChanged = _residentScene != publication;
        bool geometryChanged = !_residentGeometry || !_geometryImage.Matches(snapshot.GeometryPayloads, publication.DatabaseEpoch,
            currentDeformationBytes, previousDeformationBytes);
        int priorScene = SceneBuffer, priorGeometry = GeometryBuffer;
        if (sceneChanged)
        {
            _residentScene = default;
            _sceneImage.Pack(snapshot, _renderer.MaximumAdvancedStorageBytes);
        }
        if (geometryChanged)
        {
            _residentGeometry = false;
            _geometryImage.Pack(snapshot.GeometryPayloads, publication.DatabaseEpoch, _renderer.MaximumAdvancedStorageBytes,
                currentDeformationBytes, previousDeformationBytes);
        }
        SceneArena.EnsureCapacity(_sceneImage.Bytes.Length);
        GeometryArena.EnsureCapacity(_geometryImage.Bytes.Length);
        int sceneBytes = sceneChanged ? _sceneImage.Bytes.Length : 0;
        int geometryBytes = geometryChanged ? _geometryImage.Bytes.Length : 0;
        bool prepareImmediately = priorScene != SceneBuffer || priorGeometry != GeometryBuffer ||
            !_renderer.CanStageAdvancedStorage(sceneBytes, geometryBytes);
        // A slot cannot be selected twice for different publications in one recorded
        // frame. Immediate preparation therefore never rewrites an earlier unsubmitted
        // command's storage; all previous GPU readers have completed before this call.
        _stagedScene = false;
        _stagedGeometry = false;
        if (sceneChanged)
        {
            _residentScene = default;
            if (prepareImmediately)
            {
                SceneArena.UploadPreparation(_sceneImage.Bytes);
                _residentScene = publication;
            }
            else { SceneArena.StageUpload(_sceneImage.Bytes); _stagedScene = true; }
        }
        if (geometryChanged)
        {
            _residentGeometry = false;
            if (prepareImmediately)
            {
                GeometryArena.UploadPreparation(_geometryImage.Bytes);
                _residentGeometry = true;
            }
            else { GeometryArena.StageUpload(_geometryImage.Bytes); _stagedGeometry = true; }
        }
        _lease = lease;
        _snapshot = snapshot;
        RecordingSequence = frameSequence;
        CurrentDeformationBytes = currentDeformationBytes;
        PreviousDeformationBytes = previousDeformationBytes;
        CopiedDeformation = default;
    }

    internal void EndRecording(uint frameSequence, bool submitted)
    {
        if (RecordingSequence != frameSequence) return;
        if (submitted)
        {
            if (_stagedScene) _residentScene = Publication;
            if (_stagedGeometry) _residentGeometry = true;
            SubmittedSequence = frameSequence;
            RecordingSequence = 0;
        }
        else ReleasePublication();
        _stagedScene = false;
        _stagedGeometry = false;
    }

    internal void Reclaim(uint completedSequence)
    {
        if (SubmittedSequence != 0 && SubmittedSequence <= completedSequence)
            ReleasePublication();
    }

    private void ReleasePublication()
    {
        _lease.Dispose();
        _lease = default;
        _snapshot = null;
        RecordingSequence = 0;
        SubmittedSequence = 0;
    }

    /// <summary>Terminal disposal follows device destruction, so no pending submission can consume a released lease.</summary>
    public void Dispose()
    {
        try { SceneArena.Dispose(); }
        finally
        {
            try { GeometryArena.Dispose(); }
            finally { ReleasePublication(); }
        }
    }
}
