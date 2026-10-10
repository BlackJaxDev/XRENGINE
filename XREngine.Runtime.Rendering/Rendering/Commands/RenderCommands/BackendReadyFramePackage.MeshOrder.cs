namespace XREngine.Rendering.Commands;

public sealed partial class BackendReadyFramePackage
{
    private readonly Dictionary<int, GpuMeshSubmissionOrderPublication> _meshOrder = [];
    private GPUScene? _meshOrderScene;
    private XRCamera? _meshOrderCamera;
    private GpuMeshSubmissionOrderView _meshOrderView;
    private BackendReadyFramePackageIdentity _meshOrderIdentity;
    private long _meshOrderPackageGeneration;
    private long _meshOrderSourceRevision;
    private string? _meshOrderFailure = "RenderOrder.FullResidentCollectionRequired: no source-order publication was prepared.";

    internal void PrepareMeshOrder(Dictionary<int, GpuMeshSubmissionOrderPublication> source,
        GPUScene? scene, XRCamera? camera, in GpuMeshSubmissionOrderView view, string? failure)
    {
        ResetMeshOrder();
        _meshOrderScene = scene;
        _meshOrderCamera = camera;
        _meshOrderView = view;
        _meshOrderIdentity = Identity;
        _meshOrderPackageGeneration = PackageGeneration;
        _meshOrderSourceRevision = SourceRevision;
        _meshOrderFailure = failure;
        if (failure is not null) return;
        foreach (KeyValuePair<int, GpuMeshSubmissionOrderPublication> pass in source)
        {
            if (!_meshOrder.TryGetValue(pass.Key, out GpuMeshSubmissionOrderPublication? destination))
            {
                destination = new();
                _meshOrder.Add(pass.Key, destination);
            }
            destination.CopyFrom(pass.Value);
        }
    }

    /// <summary>Validates the consumed package identity before exposing exact sort inputs for a resident pass.</summary>
    public bool TryGetFullResidentMeshOrder(GPUScene scene, ulong frameId, XRCamera camera,
        in RenderFrameViewSelection view, int renderPass, out GpuMeshSubmissionOrderPublication? publication,
        out string reason)
    {
        publication = null;
        if (_meshOrderFailure is not null) { reason = _meshOrderFailure; return false; }
        if (State != EBackendReadyFramePackageState.Published || frameId == 0 || Identity.FrameId != frameId ||
            Identity != _meshOrderIdentity || PackageGeneration != _meshOrderPackageGeneration ||
            SourceRevision != _meshOrderSourceRevision ||
            Identity.CollectGeneration < 0 || !ReferenceEquals(scene, _meshOrderScene) ||
            !ReferenceEquals(camera, _meshOrderCamera) || view.View.SourceCameraIdentity != camera.RenderIdentity ||
            !_meshOrderView.Matches(in view))
        {
            reason = "RenderOrder.ConsumedIdentityMismatch: source ordering does not match the consumed frame, scene and frozen camera.";
            return false;
        }
        if (!_meshOrder.TryGetValue(renderPass, out publication))
        {
            reason = "RenderOrder.PassMissing: the full-resident collection published no sort inputs for this pass.";
            return false;
        }
        if (publication.SortPolicy < 0)
        {
            publication = null;
            reason = "RenderOrder.OpaqueStateBucketsUnavailable: exact state-bucket ordering requires its own shared publication.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void ResetMeshOrder()
    {
        foreach (GpuMeshSubmissionOrderPublication publication in _meshOrder.Values) publication.Reset(-1);
        _meshOrderScene = null;
        _meshOrderCamera = null;
        _meshOrderView = default;
        _meshOrderIdentity = default;
        _meshOrderPackageGeneration = 0;
        _meshOrderSourceRevision = -1;
        _meshOrderFailure = "RenderOrder.FullResidentCollectionRequired: no source-order publication was prepared.";
    }
}
