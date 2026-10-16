using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    // One entry for each palette binding of the last full bounds build, in binding order.
    // A build reuses the slots, sources, envelopes, material contracts, and work items of
    // these entries when every witness is unchanged. It then computes only the per-frame
    // spatial state of each row.
    private readonly List<RetainedBoundsBinding> _retainedBoundsBindings = [];
    private readonly List<PhysicsChainDrawMaterialSnapshot> _retainedBoundsMaterialSnapshots = [];
    private int _retainedBoundsMaterialRejectionCount;
    // True when every retained row can be reused: each row has a scene route for its
    // materials, no blendshapes, and a work-item bound equal to its current envelope.
    private bool _retainedBoundsLayoutReusable;
    // True after a successful commit of a page that the retained layout produced.
    private bool _retainedBoundsLayoutCommitted;
    private long _retainedBoundsCommittedInvalidationVersion = -1L;
    // Advances under _outputPageSync each time the pending or committed bounds sources, or the
    // palette-input bound maps, are cleared outside a commit. A commit marks the retained
    // layout committed only when this value did not change after the build read it.
    private long _boundsRowsInvalidationVersion;
    private bool _currentBoundsBuildReusedRows;
    private long _currentBoundsBuildInvalidationVersion = -1L;
    // Only the render thread writes these cumulative counts; the stage timings report them.
    private long _boundsBuildReuseCount;
    private long _boundsBuildFullCount;

    /// <summary>
    /// The retained, layout-dependent state of one palette binding. A binding that is not a
    /// bounds row keeps only its identity, so that a change of its row state is seen.
    /// </summary>
    private struct RetainedBoundsBinding
    {
        public XRMeshRenderer Renderer;
        public IPhysicsChainComputeSource Component;
        public GPUPhysicsChainRequest? Request;
        public GPUScene? Scene;
        public uint BoneMatrixElementCount;
        public long RendererBoneBufferGeneration;
        public bool DrivesCompleteBonePalette;
        public uint PaletteBase;
        public bool IsRow;
        public PhysicsChainGpuBoundsSource Source;
        public PhysicsChainMeshEnvelopeStamp Envelope;
        public PhysicsChainMaterialBoundsContract Material;
        public long MaterialRouteGeneration;
        public int MaterialSnapshotStart;
        public int MaterialSnapshotCount;
    }

    /// <summary>Marks the retained bounds layout unusable until a full build and its commit.</summary>
    /// <remarks>The caller holds <see cref="_outputPageSync"/>, or no other thread can use the dispatcher.</remarks>
    private void InvalidateRetainedBoundsRows()
    {
        ++_boundsRowsInvalidationVersion;
        _retainedBoundsLayoutCommitted = false;
    }

    /// <summary>
    /// Records the commit of the page that the current bounds build produced. The caller holds
    /// <see cref="_outputPageSync"/>. Returns true when the build reused the committed rows and no
    /// invalidation happened after the build, so the pending sources equal the committed sources.
    /// </summary>
    private bool CommitRetainedBoundsBuild()
    {
        bool unchangedSinceBuild = _currentBoundsBuildInvalidationVersion == _boundsRowsInvalidationVersion;
        bool reusedCommittedRows = _currentBoundsBuildReusedRows && unchangedSinceBuild;
        if (unchangedSinceBuild && _retainedBoundsLayoutReusable)
        {
            _retainedBoundsLayoutCommitted = true;
            _retainedBoundsCommittedInvalidationVersion = _boundsRowsInvalidationVersion;
        }
        return reusedCommittedRows;
    }

    /// <summary>
    /// Returns true when every palette binding of this frame matches its retained entry and
    /// every row witness holds: the bone buffer generation, the slice base and size, the scene,
    /// the mesh envelope inputs, and the published material route and material versions.
    /// The check does not change any state.
    /// </summary>
    private bool CanReuseRetainedBoundsRows()
    {
        if (!_retainedBoundsLayoutReusable || !_retainedBoundsLayoutCommitted ||
            _retainedBoundsCommittedInvalidationVersion != Volatile.Read(ref _boundsRowsInvalidationVersion) ||
            _retainedBoundsBindings.Count != _gpuDrivenPaletteBindings.Count)
            return false;

        ReadOnlySpan<RetainedBoundsBinding> retained = CollectionsMarshal.AsSpan(_retainedBoundsBindings);
        ReadOnlySpan<GpuDrivenRendererPaletteBinding> bindings = CollectionsMarshal.AsSpan(_gpuDrivenPaletteBindings);
        ReadOnlySpan<PhysicsChainDrawMaterialSnapshot> snapshots =
            CollectionsMarshal.AsSpan(_retainedBoundsMaterialSnapshots);
        for (int index = 0; index < retained.Length; ++index)
        {
            ref readonly RetainedBoundsBinding entry = ref retained[index];
            ref readonly GpuDrivenRendererPaletteBinding binding = ref bindings[index];
            if (!ReferenceEquals(entry.Renderer, binding.Renderer) ||
                !ReferenceEquals(entry.Component, binding.Component) ||
                !ReferenceEquals(entry.Request, binding.Request) ||
                !ReferenceEquals(entry.Scene, _gpuBoundsBindingScenes[index]) ||
                entry.BoneMatrixElementCount != binding.BoneMatrixElementCount ||
                entry.RendererBoneBufferGeneration != binding.RendererBoneBufferGeneration ||
                entry.DrivesCompleteBonePalette != binding.DrivesCompleteBonePalette ||
                entry.PaletteBase != _gpuDrivenPaletteSliceBases[index] ||
                entry.IsRow != IsBoundsRowBinding(in binding))
                return false;
            if (!entry.IsRow)
                continue;

            if (binding.Renderer.Mesh is not { } mesh || mesh.BlendshapeCount != 0u ||
                !entry.Envelope.Matches(binding.Renderer, mesh))
                return false;

            int sceneIndex = _gpuBoundsBindingSceneIndices[index];
            if (sceneIndex < 0 ||
                _gpuBoundsSceneRoutes[sceneIndex].PublicationGeneration != entry.MaterialRouteGeneration)
                return false;
            ReadOnlySpan<PhysicsChainDrawMaterialSnapshot> materials =
                snapshots.Slice(entry.MaterialSnapshotStart, entry.MaterialSnapshotCount);
            for (int material = 0; material < materials.Length; ++material)
                if (!materials[material].IsCurrent)
                    return false;
        }
        return true;
    }

    /// <summary>True when the full bounds build makes a row for this binding.</summary>
    private static bool IsBoundsRowBinding(in GpuDrivenRendererPaletteBinding binding)
        => binding.Request is { } request
            && request.ParticleOffset >= 0 && request.Particles.Count > 0
            && IsCertifiedCompleteBoundsBinding(binding)
            && binding.Renderer.BoneBufferGeneration == binding.RendererBoneBufferGeneration;

    /// <summary>
    /// Writes the per-frame output state of each retained row into the writing page. The slots,
    /// sources, work items, and allocator layout stay unchanged.
    /// </summary>
    private bool PublishRetainedBoundsRows(PhysicsChainOutputPage outputPage)
    {
        ReadOnlySpan<RetainedBoundsBinding> retained = CollectionsMarshal.AsSpan(_retainedBoundsBindings);
        for (int index = 0; index < retained.Length; ++index)
        {
            ref readonly RetainedBoundsBinding entry = ref retained[index];
            if (!entry.IsRow)
                continue;

            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[index];
            if (!IsPaletteSliceInPage(outputPage, entry.PaletteBase, entry.BoneMatrixElementCount))
            {
                _gpuBoundsFailureStage = "GpuBoundsPublication.PaletteSlice";
                return false;
            }

            // Rows without blendshapes retain no morph weights.
            int morphOffset = outputPage.MorphWeights.Count;
            AddBoundsRowOutputState(outputPage, binding.Request!, in binding, in entry.Source,
                entry.PaletteBase, entry.BoneMatrixElementCount, in entry.Envelope, in entry.Material,
                morphOffset, index);
        }

        if (_retainedBoundsMaterialRejectionCount != 0)
            for (int index = 0; index < retained.Length; ++index)
                if (retained[index].IsRow && !retained[index].Material.IsSupported)
                {
                    ReportMaterialBoundsRejection(retained[index].Renderer, in retained[index].Material);
                    break;
                }
        return true;
    }

    private static bool IsPaletteSliceInPage(PhysicsChainOutputPage outputPage, uint paletteBase, uint paletteCount)
        => paletteCount > 1u
            && paletteBase < outputPage.CurrentPalette!.ElementCount
            && paletteCount <= outputPage.CurrentPalette.ElementCount - paletteBase
            && paletteBase < outputPage.PreviousPalette!.ElementCount
            && paletteCount <= outputPage.PreviousPalette.ElementCount - paletteBase;

    /// <summary>
    /// Computes the per-frame spatial state of one bounds row and adds the renderer state and
    /// the spatial checkpoint to the writing page.
    /// </summary>
    private void AddBoundsRowOutputState(PhysicsChainOutputPage outputPage, GPUPhysicsChainRequest request,
        in GpuDrivenRendererPaletteBinding binding, in PhysicsChainGpuBoundsSource source,
        uint paletteBase, uint paletteCount, in PhysicsChainMeshEnvelopeStamp envelopeStamp,
        in PhysicsChainMaterialBoundsContract materialContract, int morphOffset, int bindingIndex)
    {
        // Read the prior published state once, by reference; the state record is large.
        ref readonly PhysicsChainGpuRendererOutputState priorState = ref _commitNotificationPriorPage is { } priorPage
            ? ref CollectionsMarshal.GetValueRefOrNullRef(priorPage.RendererStates, binding.Renderer)
            : ref Unsafe.NullRef<PhysicsChainGpuRendererOutputState>();
        bool spatialValid = TryBuildRendererSpatialBounds(outputPage, request, in binding,
            in envelopeStamp, in materialContract, in priorState, out var currentSpatial,
            out var previousSpatial, out AABB spatialBounds);
        AABB committedBounds = spatialBounds;
        ulong committedBoundsVersion = 0UL;
        if (spatialValid)
            ResolveCommittedSpatialProxy(in priorState, in source, in spatialBounds,
                out committedBounds, out committedBoundsVersion);
        var state = new PhysicsChainGpuRendererOutputState(source, paletteBase,
            paletteCount, envelopeStamp,
            !outputPage.PaletteHistoryResetRenderers.Contains(binding.Renderer),
            morphOffset, outputPage.MorphWeights.Count - morphOffset)
        {
            SpatialSource = GetSpatialSourceIdentity(request, in binding),
            CurrentSpatialState = currentSpatial,
            PreviousSpatialState = previousSpatial,
            PaletteSpatialStateValid = request.ResidentSpatialValid && request.ResidentSpatialInitialized,
            CpuSpatialBounds = committedBounds,
            CpuSpatialBoundsValid = spatialValid,
            CpuSpatialBoundsVersion = committedBoundsVersion,
            MaterialPadding = materialContract.Padding,
            MaterialBoundsSupported = materialContract.IsSupported,
            MaterialRouteScene = _gpuBoundsBindingScenes[bindingIndex],
        };
        RecordCommitNotification(binding.Renderer, in priorState, in state);
        outputPage.AddRendererState(binding.Renderer, in state);
        bool hasTightCheckpoint = request.ResidentSolveCompleted || !request.ResidentSpatialValid;
        outputPage.SpatialCheckpoints[request] = new(request.ResidentSpatialRevision,
            hasTightCheckpoint
                ? new(request.ResidentTightParticleBounds, request.ResidentTightBasisStretch)
                : currentSpatial,
            hasTightCheckpoint ? request.ResidentTightSpatialValid : request.ResidentSpatialValid);
    }

    /// <summary>Releases the retained rows and their renderer and material references.</summary>
    private void ClearRetainedBoundsRows()
    {
        _retainedBoundsBindings.Clear();
        _retainedBoundsMaterialSnapshots.Clear();
        _retainedBoundsMaterialRejectionCount = 0;
        _retainedBoundsLayoutReusable = false;
        _retainedBoundsLayoutCommitted = false;
        _currentBoundsBuildReusedRows = false;
    }
}
