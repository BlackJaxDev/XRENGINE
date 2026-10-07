using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private readonly object _cpuSpatialMaterialSync = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, CpuSpatialMaterialRouteCache>
        _cpuSpatialMaterialRoutes = new();

    private sealed class CpuSpatialMaterialRouteCache
    {
        public GPUScene? Scene;
        public long PublicationGeneration = -1;
        public readonly List<PhysicsChainDrawMaterialSnapshot> Materials = [];
    }

    /// <summary>Reads the CPU query bound of one exact committed renderer output.</summary>
    public bool TryGetCommittedSpatialBounds(XRMeshRenderer renderer, out AABB bounds, out ulong generation)
        => TryGetCommittedSpatialBounds(renderer, out bounds, out generation, out _);

    /// <summary>Gets the exact reason a committed CPU spatial bound is unavailable.</summary>
    public EPhysicsChainSpatialBoundsStatus GetCommittedSpatialBoundsStatus(XRMeshRenderer renderer)
    {
        TryGetCommittedSpatialBounds(renderer, out _, out _, out EPhysicsChainSpatialBoundsStatus status);
        return status;
    }

    private bool TryGetCommittedSpatialBounds(XRMeshRenderer renderer, out AABB bounds, out ulong generation,
        out EPhysicsChainSpatialBoundsStatus status)
    {
        status = EPhysicsChainSpatialBoundsStatus.NoPublishedOutput;
        bounds = default;
        generation = 0UL;
        GPUScene? scene = null;
        lock (_outputPageSync)
        {
            if (_publishedOutputPageIndex >= 0 &&
                !IsUsableOutputPage(_outputPages[_publishedOutputPageIndex]))
                InvalidateFailedPublishedPages();
            if (_publishedOutputPageIndex >= 0 &&
                _outputPages[_publishedOutputPageIndex].RendererStates.TryGetValue(renderer, out var retained))
                scene = retained.MaterialRouteScene;
        }
        // A scene swap can change a command override without a new physics output.
        // Read its published draw materials before admitting the retained CPU bound.
        PhysicsChainMaterialBoundsContract drawnMaterial = default;
        lock (_cpuSpatialMaterialSync)
        {
            List<PhysicsChainDrawMaterialSnapshot>? materialSnapshots = null;
            long materialPublicationGeneration = -1;
            if (scene is not null)
            {
                CpuSpatialMaterialRouteCache route = _cpuSpatialMaterialRoutes.GetValue(
                    renderer, static _ => new CpuSpatialMaterialRouteCache());
                if (!ReferenceEquals(route.Scene, scene) ||
                    !scene.TryGetRendererCommandMaterialGeneration(renderer,
                        out materialPublicationGeneration) ||
                    route.PublicationGeneration != materialPublicationGeneration)
                {
                    route.Materials.Clear();
                    bool captured = scene.TryCaptureRendererCommandMaterialSnapshots(
                        renderer, route.Materials, out materialPublicationGeneration);
                    route.Scene = scene;
                    route.PublicationGeneration = captured
                        ? materialPublicationGeneration : -1;
                }
                if (route.PublicationGeneration >= 0)
                    materialSnapshots = route.Materials;
            }
            if (materialSnapshots is not null)
            {
                for (int index = 0; index < materialSnapshots.Count; ++index)
                {
                    PhysicsChainDrawMaterialSnapshot drawn = materialSnapshots[index];
                    if (!drawn.IsCurrent)
                    {
                        drawnMaterial = new(0.0f,
                            PhysicsChainMaterialBoundsEvaluator.StalePublishedMaterialRejection);
                        break;
                    }
                    drawnMaterial = PhysicsChainMaterialBoundsContract.Combine(drawnMaterial,
                        PhysicsChainMaterialBoundsEvaluator.Evaluate(drawn.Material));
                }
            }
            else
            {
                for (int primitive = 0; renderer.TryGetMesh(primitive, out _, out XRMaterial? material); ++primitive)
                    drawnMaterial = PhysicsChainMaterialBoundsContract.Combine(drawnMaterial,
                        PhysicsChainMaterialBoundsEvaluator.Evaluate(material));
            }
            lock (_outputPageSync)
            {
                // A spatial query depends on this publication. Page maintenance
                // checks the remaining producers at the dispatch and acquire boundaries.
                bool usableOutputPage = _publishedOutputPageIndex >= 0 &&
                    IsUsableOutputPage(_outputPages[_publishedOutputPageIndex]);
                if (_publishedOutputPageIndex >= 0 && !usableOutputPage)
                    InvalidateFailedPublishedPages();
                if (_publishedOutputPageIndex < 0)
                    status = EPhysicsChainSpatialBoundsStatus.NoPublishedOutput;
                else if (!renderer.HasCompleteGpuDrivenBoneCoverage)
                    status = EPhysicsChainSpatialBoundsStatus.IncompleteBoneCoverage;
                else if (!_committedBoundsSources.TryGetValue(renderer, out PhysicsChainGpuBoundsSource source))
                    status = EPhysicsChainSpatialBoundsStatus.MissingBoundsSource;
                else
                {
                    PhysicsChainOutputPage page = _outputPages[_publishedOutputPageIndex];
                    if (!usableOutputPage)
                        status = EPhysicsChainSpatialBoundsStatus.UnavailableProducer;
                    else if (!page.RendererStates.TryGetValue(renderer, out var state))
                        status = EPhysicsChainSpatialBoundsStatus.MissingRendererState;
                    else if (state.BoundsSource != source)
                        status = EPhysicsChainSpatialBoundsStatus.BoundsSourceChanged;
                    else if (!state.CpuSpatialBoundsValid)
                        status = EPhysicsChainSpatialBoundsStatus.InvalidSpatialBounds;
                    else if (renderer.Mesh is not { } mesh)
                        status = EPhysicsChainSpatialBoundsStatus.MissingMesh;
                    else if (!ReferenceEquals(state.MaterialRouteScene, scene))
                        status = EPhysicsChainSpatialBoundsStatus.MaterialRouteChanged;
                    else if (!state.Envelope.Matches(renderer, mesh))
                        status = EPhysicsChainSpatialBoundsStatus.MeshEnvelopeChanged;
                    else if (renderer.BoneBufferGeneration != source.RendererBoneBufferGeneration)
                        status = EPhysicsChainSpatialBoundsStatus.BoneGenerationChanged;
                    else if (!state.MaterialBoundsSupported || !drawnMaterial.IsSupported)
                        status = EPhysicsChainSpatialBoundsStatus.UnsupportedMaterial;
                    else if (!float.IsFinite(drawnMaterial.Padding) || drawnMaterial.Padding > state.MaterialPadding)
                        status = EPhysicsChainSpatialBoundsStatus.MaterialPaddingChanged;
                    else if (materialSnapshots is not null &&
                        !AreCpuSpatialMaterialSnapshotsCurrent(materialSnapshots))
                        status = EPhysicsChainSpatialBoundsStatus.StaleMaterialSnapshot;
                    else
                    {
                        status = EPhysicsChainSpatialBoundsStatus.Ready;
                        bounds = state.CpuSpatialBounds;
                        generation = page.ProducerEpoch;
                    }
                }
            }
            if (status == EPhysicsChainSpatialBoundsStatus.Ready && scene is not null)
            {
                bool hasCurrentRoute = scene.TryGetRendererCommandMaterialGeneration(
                    renderer, out long currentGeneration);
                if (currentGeneration != materialPublicationGeneration ||
                    hasCurrentRoute != (materialSnapshots is not null))
                    status = EPhysicsChainSpatialBoundsStatus.MaterialRouteChanged;
                else if (materialSnapshots is not null &&
                    !AreCpuSpatialMaterialSnapshotsCurrent(materialSnapshots))
                    status = EPhysicsChainSpatialBoundsStatus.StaleMaterialSnapshot;
            }
            if (status == EPhysicsChainSpatialBoundsStatus.Ready)
                return true;
        }
        bounds = default;
        generation = 0UL;
        return false;
    }

    private static bool AreCpuSpatialMaterialSnapshotsCurrent(
        List<PhysicsChainDrawMaterialSnapshot> materialSnapshots)
    {
        for (int index = 0; index < materialSnapshots.Count; ++index)
            if (!materialSnapshots[index].IsCurrent)
                return false;
        return true;
    }

    private bool ConfirmResidentSpatialState()
    {
        bool changed = false;
        lock (_outputPageSync)
        {
            foreach (PhysicsChainOutputPage page in _outputPages)
            {
                if (!page.Published || page.ProducerFence is not { } fence || fence.IsDisposed ||
                    fence.SubmissionStatus != EGpuFenceSubmissionStatus.Submitted)
                    continue;
                EGpuFenceStatus status = fence.Poll();
                if (status == EGpuFenceStatus.Failed)
                {
                    page.KnownProducerFailure = true;
                    continue;
                }
                if (status != EGpuFenceStatus.Signaled)
                    continue;
                foreach (var entry in page.SpatialCheckpoints)
                {
                    GPUPhysicsChainRequest request = entry.Key;
                    PhysicsChainResidentSpatialCheckpoint checkpoint = entry.Value;
                    if (request.ResidentSpatialRevision != checkpoint.Revision)
                        continue;
                    changed |= request.ResidentSpatialValid != checkpoint.IsValid ||
                        request.ResidentParticleBounds.Min != checkpoint.State.ParticleBounds.Min ||
                        request.ResidentParticleBounds.Max != checkpoint.State.ParticleBounds.Max ||
                        request.ResidentBasisStretch != checkpoint.State.MaximumBasisStretch;
                    request.ResidentParticleBounds = checkpoint.State.ParticleBounds;
                    request.ResidentBasisStretch = checkpoint.State.MaximumBasisStretch;
                    request.ResidentSpatialValid = checkpoint.IsValid;
                }
            }
            InvalidateFailedPublishedPages();
        }
        return changed;
    }

    private static void ObserveResidentSpatialInputs(GPUPhysicsChainRequest request,
        bool particleUpload, bool transformUpload)
    {
        request.ResidentSolveAttempted = false;
        request.ResidentSolveCompleted = false;
        if (!particleUpload && !transformUpload)
            return;
        ++request.ResidentSpatialRevision;
        var input = request.SpatialInput;
        if (!input.IsValid)
        {
            request.ResidentSpatialValid = false;
            request.ResidentTightSpatialValid = false;
            return;
        }
        if (particleUpload)
        {
            request.ResidentParticleBounds = request.ResidentSpatialInitialized
                ? UnionSpatialBounds(request.ResidentParticleBounds, input.SeedParticleBounds)
                : input.SeedParticleBounds;
            request.ResidentSpatialValid = !request.ResidentSpatialInitialized || request.ResidentSpatialValid;
            request.ResidentSpatialInitialized = true;
            request.ResidentTightParticleBounds = input.SeedParticleBounds;
            request.ResidentTightSpatialValid = true;
        }
        if (transformUpload)
        {
            request.ResidentBasisStretch = MathF.Max(request.ResidentBasisStretch, input.MaximumBasisStretch);
            request.ResidentTightBasisStretch = input.MaximumBasisStretch;
        }
    }

    private void ObserveResidentSpatialSolveAttempt(GPUPhysicsChainRequest request)
    {
        // This predicate matches the uploaded active-work metadata and unsigned cadence arithmetic.
        uint cadence = Math.Max(request.Cadence, 1u);
        if (request.Enabled == 0u || request.Relevant == 0u || request.SleepState != 0u ||
            request.LoopCount <= 0 || unchecked(_activeWorkFrameIndex + request.Phase) % cadence != 0u)
            return;
        request.ResidentSolveAttempted = true;
        ++request.ResidentSpatialRevision;
        if (!request.SpatialInput.IsValid || !request.ResidentSpatialInitialized)
        {
            request.ResidentSpatialValid = false;
            request.ResidentTightSpatialValid = false;
            return;
        }
        // A failed native batch can leave some writes resident. Keep both possible positions.
        request.ResidentParticleBounds = UnionSpatialBounds(request.ResidentParticleBounds,
            request.SpatialInput.RootReachBounds);
        request.ResidentTightParticleBounds = request.SpatialInput.RootReachBounds;
    }

    private static void CompleteResidentSpatialSolveAttempt(GPUPhysicsChainRequest request)
        => request.ResidentSolveCompleted = request.ResidentSolveAttempted;

    private static PhysicsChainSpatialSourceIdentity GetSpatialSourceIdentity(GPUPhysicsChainRequest request,
        in GpuDrivenRendererPaletteBinding binding)
        => new(request.RequestId, request.ExecutionGeneration, request.StaticDataVersion,
            request.ParticleStateVersion, binding.BindingGeneration, binding.RendererBoneBufferGeneration);

    private static bool TryBuildRendererSpatialBounds(PhysicsChainOutputPage page,
        GPUPhysicsChainRequest request, in GpuDrivenRendererPaletteBinding binding,
        in PhysicsChainMeshEnvelopeStamp envelope, in PhysicsChainMaterialBoundsContract material,
        PhysicsChainOutputPage? priorPage, out PhysicsChainGpuRendererOutputState priorState,
        out PhysicsChainPaletteSpatialState current, out PhysicsChainPaletteSpatialState previous,
        out AABB bounds)
    {
        current = new(request.ResidentParticleBounds, request.ResidentBasisStretch);
        previous = current;
        priorState = default;
        bounds = default;
        if (!request.ResidentSpatialValid || !request.ResidentSpatialInitialized || !material.IsSupported ||
            !float.IsFinite(envelope.VertexInfluenceRadius))
            return false;

        float radius = envelope.VertexInfluenceRadius;
        if (!page.PaletteHistoryResetRenderers.Contains(binding.Renderer) && priorPage is not null &&
            priorPage.RendererStates.TryGetValue(binding.Renderer, out priorState) &&
            priorState.SpatialSource == GetSpatialSourceIdentity(request, in binding))
        {
            if (!priorState.PaletteSpatialStateValid)
                return false;
            previous = priorState.CurrentSpatialState;
            radius = MathF.Max(radius, priorState.Envelope.VertexInfluenceRadius);
        }
        if (!TryExpandSpatialBounds(in current, radius, material.Padding, out AABB currentBounds) ||
            !TryExpandSpatialBounds(in previous, radius, material.Padding, out AABB previousBounds))
            return false;
        bounds = UnionSpatialBounds(currentBounds, previousBounds);
        return true;
    }

    private static bool TryExpandSpatialBounds(in PhysicsChainPaletteSpatialState state,
        float radius, float materialPadding, out AABB bounds)
    {
        Vector3 minimum = state.ParticleBounds.Min;
        Vector3 maximum = state.ParticleBounds.Max;
        float padding = radius * state.MaximumBasisStretch + materialPadding;
        Vector3 numericMargin = Vector3.Max(Vector3.Abs(minimum), Vector3.Abs(maximum)) * 0.0001f
            + new Vector3(0.0001f);
        Vector3 extent = new Vector3(MathF.BitIncrement(padding)) + numericMargin;
        bounds = new(minimum - extent, maximum + extent);
        return padding >= 0.0f && float.IsFinite(padding) && bounds.IsValid &&
            IsFiniteBoundsVector(bounds.Min) && IsFiniteBoundsVector(bounds.Max);
    }

    private static AABB UnionSpatialBounds(in AABB first, in AABB second)
        => new(Vector3.Min(first.Min, second.Min), Vector3.Max(first.Max, second.Max));

    private static void NotifyCommittedSpatialBoundsChanged(PhysicsChainOutputPage page, bool published)
    {
        foreach (XRMeshRenderer renderer in page.RendererStates.Keys)
            renderer.NotifyCommittedWorldBoundsChanged(page.ProducerEpoch, published);
    }
}
