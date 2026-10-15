using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private readonly object _cpuSpatialMaterialSync = new();
    private readonly ConditionalWeakTable<XRMeshRenderer, CpuSpatialMaterialRouteCache>
        _cpuSpatialMaterialRoutes = new();
    // Written only while the render thread builds an output page.
    private ulong _committedSpatialBoundsVersion;

    // Advances under _outputPageSync when a published value that a committed spatial query
    // reads can change: a renderer's committed page state, the committed bounds sources, or
    // the published page itself. A page commit with unchanged renderer values keeps the epoch.
    private long _committedSpatialEpoch;

    private sealed class CpuSpatialMaterialRouteCache
    {
        public GPUScene? Scene;
        public long PublicationGeneration = -1;
        public readonly List<PhysicsChainDrawMaterialSnapshot> Materials = [];
    }

    /// <summary>
    /// Reads the committed CPU bound of one renderer and its version. The bound is an enlarged
    /// proxy of the exact output bound. The version changes only when this renderer's committed
    /// bound changes, not on every physics output.
    /// </summary>
    /// <remarks>
    /// A repeated query reuses the renderer's last result while its witnesses are unchanged
    /// (see <see cref="PhysicsChainCommittedSpatialQuery"/>). The reuse takes only the renderer's
    /// own lock, so collection and scene publication do not contend with the render thread.
    /// A resolve holds the query lock while it takes <c>_outputPageSync</c>,
    /// <c>_cpuSpatialMaterialSync</c>, and the scene lock. Code that holds one of those locks
    /// must not wait for a query lock.
    /// </remarks>
    public bool TryGetCommittedSpatialBounds(XRMeshRenderer renderer, out AABB bounds, out ulong generation)
    {
        PhysicsChainCommittedSpatialQuery query = renderer.CommittedSpatialQuery;
        lock (query)
        {
            if (!TryReuseCommittedSpatialQuery(renderer, query))
                ResolveCommittedSpatialQuery(renderer, query);

            if (query.Status == EPhysicsChainSpatialBoundsStatus.Ready)
            {
                bounds = query.Bounds;
                generation = query.Version;
                return true;
            }
        }
        bounds = default;
        generation = 0UL;
        return false;
    }

    /// <summary>Gets the exact reason a committed CPU spatial bound is unavailable.</summary>
    public EPhysicsChainSpatialBoundsStatus GetCommittedSpatialBoundsStatus(XRMeshRenderer renderer)
    {
        TryGetCommittedSpatialBounds(renderer, out _, out _, out EPhysicsChainSpatialBoundsStatus status, query: null);
        return status;
    }

    /// <summary>Gets the current dispatcher spatial epoch.</summary>
    internal long CommittedSpatialEpoch => Volatile.Read(ref _committedSpatialEpoch);

    /// <summary>
    /// Reads the spatial epoch and checks the published output page without
    /// <c>_outputPageSync</c>. Scene publication calls this one time for each
    /// publication to prove that the committed bounds sources did not change.
    /// A false result sends the caller to the locked capture path, which
    /// handles a failed page.
    /// </summary>
    internal bool TryReadCommittedBoundsEpoch(out long epoch)
    {
        // Read the epoch first. A source change after this read advances it.
        epoch = Volatile.Read(ref _committedSpatialEpoch);
        return IsPublishedOutputPageUsableUnlocked();
    }

    // Indexed by SpatialQueryOutcome, then three epoch-advance causes.
    private readonly long[] _committedSpatialQueryCounters = new long[12];

    private enum SpatialQueryOutcome
    {
        Reused,
        Unfilled,
        EpochChanged,
        PublishedPageChanged,
        RendererChanged,
        MeshChanged,
        NoSceneRoute,
        ScenePublicationChanged,
        MaterialChanged,
    }

    private enum SpatialEpochCause
    {
        Sources = 9,
        RendererState = 10,
        Invalidation = 11,
    }

    /// <summary>Captures cumulative committed spatial query outcomes of the shared dispatcher.</summary>
    public static PhysicsChainCommittedSpatialQueryDiagnostics CaptureCommittedSpatialQueryDiagnostics()
        => Instance.GetCommittedSpatialQueryDiagnostics();

    /// <summary>Captures cumulative committed spatial query outcomes and epoch-advance causes.</summary>
    public PhysicsChainCommittedSpatialQueryDiagnostics GetCommittedSpatialQueryDiagnostics()
    {
        long[] c = _committedSpatialQueryCounters;
        return new(Volatile.Read(ref c[0]), Volatile.Read(ref c[1]), Volatile.Read(ref c[2]),
            Volatile.Read(ref c[3]), Volatile.Read(ref c[4]), Volatile.Read(ref c[5]),
            Volatile.Read(ref c[6]), Volatile.Read(ref c[7]), Volatile.Read(ref c[8]),
            Volatile.Read(ref c[9]), Volatile.Read(ref c[10]), Volatile.Read(ref c[11]));
    }

    /// <summary>Invalidates every reusable committed spatial query. Call under <c>_outputPageSync</c>.</summary>
    private void AdvanceCommittedSpatialEpoch(SpatialEpochCause cause)
    {
        Interlocked.Increment(ref _committedSpatialEpoch);
        Interlocked.Increment(ref _committedSpatialQueryCounters[(int)cause]);
    }

    /// <summary>
    /// Checks the published page without <c>_outputPageSync</c>. Failure handling stays with
    /// the locked paths; this read only decides whether a reusable result still applies.
    /// </summary>
    private bool IsPublishedOutputPageUsableUnlocked()
    {
        int index = Volatile.Read(ref _publishedOutputPageIndex);
        PhysicsChainOutputPage[] pages = Volatile.Read(ref _outputPages);
        return index >= 0 && index < pages.Length && IsUsableOutputPage(pages[index]);
    }

    /// <summary>Returns true when the renderer's last resolved query still applies.</summary>
    private bool TryReuseCommittedSpatialQuery(XRMeshRenderer renderer, PhysicsChainCommittedSpatialQuery query)
    {
        SpatialQueryOutcome outcome = EvaluateCommittedSpatialQueryReuse(renderer, query);
        if (outcome == SpatialQueryOutcome.Reused)
            return true;
        // Only misses are counted, so a reuse writes no shared memory.
        Interlocked.Increment(ref _committedSpatialQueryCounters[(int)outcome]);
        return false;
    }

    private SpatialQueryOutcome EvaluateCommittedSpatialQueryReuse(XRMeshRenderer renderer,
        PhysicsChainCommittedSpatialQuery query)
    {
        if (!query.Filled || !ReferenceEquals(query.Dispatcher, this))
            return SpatialQueryOutcome.Unfilled;
        if (query.DispatcherEpoch != Volatile.Read(ref _committedSpatialEpoch))
            return SpatialQueryOutcome.EpochChanged;
        if (query.PublishedPageUsable != IsPublishedOutputPageUsableUnlocked())
            return SpatialQueryOutcome.PublishedPageChanged;
        if (query.CoverageComplete != renderer.HasCompleteGpuDrivenBoneCoverage ||
            query.BoneBufferGeneration != renderer.BoneBufferGeneration)
            return SpatialQueryOutcome.RendererChanged;

        XRMesh? mesh = renderer.Mesh;
        if (!ReferenceEquals(mesh, query.Mesh) ||
            query.HasEnvelope && (mesh is null || !query.Envelope.Matches(renderer, mesh)))
            return SpatialQueryOutcome.MeshChanged;

        // Direct renderer materials have no publication witness, so only scene-routed results
        // are reusable. The drawn materials must still have the captured state and contracts.
        if (query.Scene is not { } scene)
            return SpatialQueryOutcome.NoSceneRoute;
        if (scene.RendererCommandMaterialPublicationGeneration != query.SceneGeneration)
            return SpatialQueryOutcome.ScenePublicationChanged;
        if (!AreCpuSpatialMaterialSnapshotsCurrent(query.Materials))
            return SpatialQueryOutcome.MaterialChanged;

        PhysicsChainMaterialBoundsContract drawnMaterial = default;
        for (int index = 0; index < query.Materials.Count; ++index)
            drawnMaterial = PhysicsChainMaterialBoundsContract.Combine(drawnMaterial,
                PhysicsChainMaterialBoundsEvaluator.Evaluate(query.Materials[index].Material));
        return drawnMaterial == query.DrawnMaterial
            ? SpatialQueryOutcome.Reused
            : SpatialQueryOutcome.MaterialChanged;
    }

    /// <summary>Resolves the query under the dispatcher locks and records its witnesses.</summary>
    private void ResolveCommittedSpatialQuery(XRMeshRenderer renderer, PhysicsChainCommittedSpatialQuery query)
    {
        query.Clear();
        // Read the epoch first. A change after this read advances it, so a later reuse fails.
        long epoch = Volatile.Read(ref _committedSpatialEpoch);
        query.Dispatcher = this;
        query.DispatcherEpoch = epoch;
        query.CoverageComplete = renderer.HasCompleteGpuDrivenBoneCoverage;
        query.BoneBufferGeneration = renderer.BoneBufferGeneration;
        query.Mesh = renderer.Mesh;
        TryGetCommittedSpatialBounds(renderer, out query.Bounds, out query.Version,
            out query.Status, query);
        query.Filled = true;
    }

    private bool TryGetCommittedSpatialBounds(XRMeshRenderer renderer, out AABB bounds, out ulong generation,
        out EPhysicsChainSpatialBoundsStatus status, PhysicsChainCommittedSpatialQuery? query)
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
                bool hasSource = _committedBoundsSources.TryGetValue(renderer, out PhysicsChainGpuBoundsSource source);
                if (query is not null)
                {
                    query.PublishedPageUsable = _publishedOutputPageIndex >= 0 && usableOutputPage;
                    query.HasBoundsSource = hasSource;
                    query.BoundsSource = source;
                }
                if (_publishedOutputPageIndex < 0)
                    status = EPhysicsChainSpatialBoundsStatus.NoPublishedOutput;
                else if (!renderer.HasCompleteGpuDrivenBoneCoverage)
                    status = EPhysicsChainSpatialBoundsStatus.IncompleteBoneCoverage;
                else if (!hasSource)
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
                    else
                    {
                        if (query is not null)
                        {
                            query.HasEnvelope = true;
                            query.Envelope = state.Envelope;
                        }
                        if (!state.Envelope.Matches(renderer, mesh))
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
                            generation = state.CpuSpatialBoundsVersion;
                        }
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
            if (query is not null)
            {
                query.DrawnMaterial = drawnMaterial;
                // Only a scene-routed result has a publication witness for later reuse.
                if (scene is not null && materialSnapshots is not null)
                {
                    query.Scene = scene;
                    query.SceneGeneration = materialPublicationGeneration;
                    query.Materials.AddRange(materialSnapshots);
                }
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

    /// <param name="priorState">The renderer's state on the prior published page, or a null reference.</param>
    private static bool TryBuildRendererSpatialBounds(PhysicsChainOutputPage page,
        GPUPhysicsChainRequest request, in GpuDrivenRendererPaletteBinding binding,
        in PhysicsChainMeshEnvelopeStamp envelope, in PhysicsChainMaterialBoundsContract material,
        ref readonly PhysicsChainGpuRendererOutputState priorState,
        out PhysicsChainPaletteSpatialState current, out PhysicsChainPaletteSpatialState previous,
        out AABB bounds)
    {
        current = new(request.ResidentParticleBounds, request.ResidentBasisStretch);
        previous = current;
        bounds = default;
        if (!request.ResidentSpatialValid || !request.ResidentSpatialInitialized || !material.IsSupported ||
            !float.IsFinite(envelope.VertexInfluenceRadius))
            return false;

        float radius = envelope.VertexInfluenceRadius;
        if (!Unsafe.IsNullRef(in priorState) &&
            !page.PaletteHistoryResetRenderers.Contains(binding.Renderer) &&
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

    /// <summary>
    /// Keeps the prior committed proxy and its version while the exact bound stays inside it.
    /// Otherwise fits a new proxy with a new version, so each version names one proxy bound.
    /// </summary>
    /// <param name="prior">The renderer's state on the prior published page, or a null reference.</param>
    private void ResolveCommittedSpatialProxy(ref readonly PhysicsChainGpuRendererOutputState prior,
        in PhysicsChainGpuBoundsSource source, in AABB exactBounds, out AABB proxy, out ulong version)
    {
        if (!Unsafe.IsNullRef(in prior)
            && prior.CpuSpatialBoundsValid
            && prior.BoundsSource == source
            && PhysicsChainCommittedSpatialProxy.CanKeep(prior.CpuSpatialBounds, exactBounds))
        {
            proxy = prior.CpuSpatialBounds;
            version = prior.CpuSpatialBoundsVersion;
            return;
        }
        proxy = PhysicsChainCommittedSpatialProxy.Create(exactBounds);
        version = ++_committedSpatialBoundsVersion;
    }

    /// <summary>
    /// Collects the commit notification of each renderer on a newly published
    /// <paramref name="page"/>. A renderer's committed bound changed only when its validity or
    /// version differs from <paramref name="priorPage"/>. Returns true when any value that a
    /// committed spatial query reads differs from <paramref name="priorPage"/>.
    /// </summary>
    private static bool CollectCommittedSpatialNotifications(PhysicsChainOutputPage page,
        PhysicsChainOutputPage? priorPage, List<(XRMeshRenderer Renderer, bool BoundsChanged)> notifications)
    {
        bool queryStateChanged = priorPage is null ||
            priorPage.RendererStates.Count != page.RendererStates.Count;
        foreach (XRMeshRenderer renderer in page.RendererStates.Keys)
        {
            // Read both states by reference; the state record is large.
            ref PhysicsChainGpuRendererOutputState state =
                ref CollectionsMarshal.GetValueRefOrNullRef(page.RendererStates, renderer);
            ref PhysicsChainGpuRendererOutputState prior = ref priorPage is null
                ? ref Unsafe.NullRef<PhysicsChainGpuRendererOutputState>()
                : ref CollectionsMarshal.GetValueRefOrNullRef(priorPage.RendererStates, renderer);
            bool hasPrior = !Unsafe.IsNullRef(ref prior);
            bool boundsChanged = !hasPrior
                || prior.CpuSpatialBoundsValid != state.CpuSpatialBoundsValid
                || prior.CpuSpatialBoundsVersion != state.CpuSpatialBoundsVersion;
            queryStateChanged |= boundsChanged || !HasSameCommittedQueryState(in prior, in state);
            notifications.Add((renderer, boundsChanged));
        }
        return queryStateChanged;
    }

    /// <summary>Compares the page values that a committed spatial query reads for one renderer.</summary>
    private static bool HasSameCommittedQueryState(in PhysicsChainGpuRendererOutputState prior,
        in PhysicsChainGpuRendererOutputState current)
        => prior.BoundsSource == current.BoundsSource
            && prior.CpuSpatialBoundsValid == current.CpuSpatialBoundsValid
            && prior.CpuSpatialBoundsVersion == current.CpuSpatialBoundsVersion
            && prior.CpuSpatialBounds.Min == current.CpuSpatialBounds.Min
            && prior.CpuSpatialBounds.Max == current.CpuSpatialBounds.Max
            && ReferenceEquals(prior.MaterialRouteScene, current.MaterialRouteScene)
            && prior.MaterialPadding.Equals(current.MaterialPadding)
            && prior.MaterialBoundsSupported == current.MaterialBoundsSupported
            && prior.Envelope == current.Envelope;

    /// <summary>Returns true when both maps hold the same bounds source for the same renderers.</summary>
    private static bool HaveSameBoundsSources(Dictionary<XRMeshRenderer, PhysicsChainGpuBoundsSource> committed,
        Dictionary<XRMeshRenderer, PhysicsChainGpuBoundsSource> pending)
    {
        if (committed.Count != pending.Count)
            return false;
        foreach (KeyValuePair<XRMeshRenderer, PhysicsChainGpuBoundsSource> entry in pending)
            if (!committed.TryGetValue(entry.Key, out PhysicsChainGpuBoundsSource source) || source != entry.Value)
                return false;
        return true;
    }

    /// <summary>
    /// Reports a new or withdrawn output to each renderer on <paramref name="page"/>. A renderer's
    /// committed bound changed only when its validity or version differs from
    /// <paramref name="priorPage"/>, so a renderer with an unchanged bound keeps its CPU tree placement.
    /// </summary>
    private static void NotifyCommittedSpatialOutputChanged(PhysicsChainOutputPage page, bool published,
        PhysicsChainOutputPage? priorPage)
    {
        foreach (KeyValuePair<XRMeshRenderer, PhysicsChainGpuRendererOutputState> entry in page.RendererStates)
        {
            bool boundsChanged = !published || priorPage is null
                || !priorPage.RendererStates.TryGetValue(entry.Key, out PhysicsChainGpuRendererOutputState prior)
                || prior.CpuSpatialBoundsValid != entry.Value.CpuSpatialBoundsValid
                || prior.CpuSpatialBoundsVersion != entry.Value.CpuSpatialBoundsVersion;
            entry.Key.NotifyCommittedOutputChanged(page.ProducerEpoch, published, boundsChanged);
        }
    }
}
