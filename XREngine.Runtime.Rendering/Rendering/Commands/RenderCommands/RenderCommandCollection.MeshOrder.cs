using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection
{
    private const int MaximumOrderPasses = 256;
    private readonly Dictionary<int, GpuMeshSubmissionOrderPublication> _updatingMeshOrder = [];
    private GPUScene? _updatingOrderScene;
    private XRCamera? _updatingOrderCamera;
    private GpuMeshSubmissionOrderView _updatingOrderView;
    private ulong _updatingOrderFrameId;
    private long _updatingOrderCollectGeneration;
    private int _updatingOrderScopeDepth;
    private bool _updatingOrderStarted;
    private bool _updatingOrderCompleted;
    private string? _updatingOrderFailure;

    /// <summary>Chooses full resident collection for this cooked output without changing the world's desktop culling preference.</summary>
    internal bool RequiresFullResidentAuthoredCollection
    {
        get
        {
            if (RuntimeEngineMaterialConstructionServices.Target != EngineMaterialConstructionTarget.WebGpuCooked ||
                _ownerPipeline is not XRRenderPipelineInstance owner || IsOwnedByShadowPipeline)
                return false;
            XRViewport? viewport = owner.RenderState.WindowViewport ?? owner.LastWindowViewport;
            if (viewport?.MeshSubmissionStrategyOverride is { } strategy)
                return strategy != EMeshSubmissionStrategy.CpuDirect;
            return owner.Pipeline?.RequiresGpuMeshSubmissionPublication == true;
        }
    }

    internal void BeginFullResidentMeshOrderCollection(GPUScene scene, IRuntimeRenderCamera? camera, bool fullResident)
    {
        using (_lock.EnterScope())
        {
            ulong frameId = RuntimeRenderingHostServices.FrameTiming.CollectFrameId;
            long collectGeneration = RuntimeRenderingHostServices.FrameTiming.RequestedCollectGeneration;
            if (!fullResident || camera is not XRCamera sourceCamera || frameId == 0)
                InvalidateMeshOrderNoLock("RenderOrder.FullResidentCollectionRequired: source ordering requires an unfiltered GPU collection with an identified camera and frame.");
            else if (_updatingOrderScopeDepth != 0)
                InvalidateMeshOrderNoLock("RenderOrder.NestedCollection: nested command collection has no complete source-order proof.");
            else if (_updatingOrderStarted)
            {
                if (!ReferenceEquals(_updatingOrderScene, scene) || !ReferenceEquals(_updatingOrderCamera, sourceCamera) ||
                    _updatingOrderFrameId != frameId || _updatingOrderCollectGeneration != collectGeneration ||
                    _updatingOrderView != GpuMeshSubmissionOrderView.Capture(sourceCamera, IsShadowPass))
                    InvalidateMeshOrderNoLock("RenderOrder.CollectionIdentityChanged: one package combines different scene, camera or frame collection identities.");
            }
            else
            {
                if (_numCommandsRecentlyAddedToUpdate != 0)
                    InvalidateMeshOrderNoLock("RenderOrder.PartialCollection: commands preceded the verified full-resident collection.");
                _updatingOrderScene = scene;
                _updatingOrderCamera = sourceCamera;
                _updatingOrderView = GpuMeshSubmissionOrderView.Capture(sourceCamera, IsShadowPass);
                _updatingOrderFrameId = frameId;
                _updatingOrderCollectGeneration = collectGeneration;
            }
            _updatingOrderStarted = true;
            _updatingOrderCompleted = false;
            _updatingOrderScopeDepth++;
            _updatingRevision++;
        }
    }

    internal void EndFullResidentMeshOrderCollection(bool completed)
    {
        using (_lock.EnterScope())
        {
            if (_updatingOrderScopeDepth > 0) _updatingOrderScopeDepth--;
            if (!completed)
                InvalidateMeshOrderNoLock("RenderOrder.CollectionInterrupted: full-resident source collection did not complete.");
            if (_updatingOrderCamera is { } camera && _updatingOrderView != GpuMeshSubmissionOrderView.Capture(camera, IsShadowPass))
                InvalidateMeshOrderNoLock("RenderOrder.CameraChangedDuringCollection: one source-order collection used multiple camera views.");
            _updatingOrderCompleted = completed && _updatingOrderScopeDepth == 0;
            _updatingRevision++;
        }
    }

    internal void InvalidateFullResidentMeshOrderCollection()
    {
        using (_lock.EnterScope())
            InvalidateMeshOrderNoLock("RenderOrder.CpuVisibilitySubset: CPU-frustum or masked collection cannot publish full-resident ordering.");
    }

    private void InvalidateMeshOrderNoLock(string reason)
    {
        if (_updatingOrderFailure is not null) return;
        _updatingOrderFailure = reason;
        _updatingRevision++;
    }

    private void ObserveMeshOrderInsertionNoLock(RenderCommand command, IRuntimeRenderCamera? camera,
        int pass, long sortOrderKey, AABB? bounds, Vector3 fallbackPosition, int priority)
    {
        if (_updatingOrderScopeDepth != 1)
        {
            InvalidateMeshOrderNoLock("RenderOrder.PartialCollection: a command was inserted outside the verified full-resident collection.");
            return;
        }
        if (_updatingOrderFailure is not null || command is not IRenderCommandMesh source) return;
        if (command.GetType() != typeof(RenderCommandMesh3D) || !ReferenceEquals(camera, _updatingOrderCamera) ||
            _updatingOrderCamera is null || _updatingOrderView != GpuMeshSubmissionOrderView.Capture(_updatingOrderCamera, IsShadowPass))
        {
            InvalidateMeshOrderNoLock("RenderOrder.SourceProfile: a collected mesh lacks exact three-dimensional camera sort inputs.");
            return;
        }
        _passSorterTypes.TryGetValue(pass, out Type? sorterType);
        if (sorterType == typeof(OpaqueStateBucketRenderCommandSorter))
        {
            // This failure belongs to this pass only; transparent passes remain provable.
            if (_updatingMeshOrder.TryGetValue(pass, out GpuMeshSubmissionOrderPublication? unsupported))
                unsupported.Reset(-1);
            else if (_updatingMeshOrder.Count < MaximumOrderPasses)
            {
                unsupported = new();
                unsupported.Reset(-1);
                _updatingMeshOrder.Add(pass, unsupported);
            }
            return;
        }
        int policy = sorterType is null ? 0 : sorterType == typeof(FarToNearRenderCommandSorter) ? 2 : 1;
        if (!_updatingMeshOrder.TryGetValue(pass, out GpuMeshSubmissionOrderPublication? publication))
        {
            if (_updatingMeshOrder.Count == MaximumOrderPasses)
            {
                InvalidateMeshOrderNoLock("RenderOrder.PassCapacity: the source-order publication exceeds 256 retained passes.");
                return;
            }
            publication = new();
            _updatingMeshOrder.Add(pass, publication);
        }
        if (publication.SortPolicy != policy || publication.UsePriority != (policy == 2 && pass == (int)EDefaultRenderPass.TransparentForward))
            publication.Reset(policy, policy == 2 && pass == (int)EDefaultRenderPass.TransparentForward);
        ulong insertion = checked((ulong)(policy == 2 ? long.MaxValue - sortOrderKey : sortOrderKey));
        GpuMeshSubmissionOrderSource entry = new(source, insertion, bounds?.Min ?? default,
            bounds?.Max ?? default, fallbackPosition, bounds.HasValue, priority);
        if (!publication.TryAdd(in entry))
            InvalidateMeshOrderNoLock("RenderOrder.SourceCapacityOrDuplicate: repeated source insertion or excessive resident source membership cannot be represented exactly.");
    }

    private void ResetMeshOrderCollectionNoLock()
    {
        foreach (GpuMeshSubmissionOrderPublication publication in _updatingMeshOrder.Values) publication.Reset();
        _updatingOrderScene = null;
        _updatingOrderCamera = null;
        _updatingOrderView = default;
        _updatingOrderFrameId = 0;
        _updatingOrderCollectGeneration = 0;
        _updatingOrderScopeDepth = 0;
        _updatingOrderStarted = false;
        _updatingOrderCompleted = false;
        _updatingOrderFailure = null;
    }

    private void PrepareMeshOrderPublicationNoLock(GPUScene? scene, XRCamera? camera)
    {
        string? failure = _updatingOrderFailure;
        if (!_updatingOrderStarted || !_updatingOrderCompleted || _updatingOrderScopeDepth != 0)
            failure ??= "RenderOrder.FullResidentCollectionRequired: this package has no completed full-resident source-order collection.";
        if (!ReferenceEquals(scene, _updatingOrderScene) || !ReferenceEquals(camera, _updatingOrderCamera) ||
            _updatingBackendReadyIdentity.FrameId != _updatingOrderFrameId ||
            _updatingBackendReadyIdentity.CollectGeneration != _updatingOrderCollectGeneration)
            failure ??= "RenderOrder.PackageIdentityMismatch: source order and the prepared package do not identify the same scene, camera and collection frame.";
        _updatingBackendReadyPackage.PrepareMeshOrder(_updatingMeshOrder, _updatingOrderScene,
            _updatingOrderCamera, in _updatingOrderView, failure);
    }
}
