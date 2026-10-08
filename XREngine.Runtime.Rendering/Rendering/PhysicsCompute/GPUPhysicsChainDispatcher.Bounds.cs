using System.Runtime.InteropServices;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Components;

using XREngine.Rendering.Models.Materials;
namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const uint BoundsWorkgroupSize = 256u;
    private const uint BoundsCopyWorkgroupSize = 128u;

    /// <summary>Work-item flag: the drawn materials reject the bounds contract.</summary>
    internal const uint BoundsWorkItemRejectedMaterialContract = 1u;

    private readonly PhysicsChainPaletteAtlasAllocator _gpuBoundsSlotAllocator = new();
    private readonly List<PhysicsChainGpuBoundsWorkItem> _gpuBoundsWorkItems = [];
    private readonly List<PhysicsChainGpuBoundsCopyItem> _gpuBoundsCopyItems = [];
    private readonly List<uint> _gpuBoundsCommandScratch = [];
    private readonly List<GpuSceneRendererCommandIndexSnapshot> _gpuBoundsSceneRoutes = [];
    private readonly List<PhysicsChainDrawMaterialSnapshot> _gpuBoundsMaterialScratch = [];
    private readonly List<GPUScene> _gpuBoundsScenes = [];
    private readonly HashSet<XRMeshRenderer> _gpuBoundsCopiedRenderers =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRMeshRenderer, uint> _gpuBoundsSlotByRenderer =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IPhysicsChainComputeSource, GPUPhysicsChainRequest> _gpuBoundsRequestByComponent =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<uint> _gpuBoundsSlotMetadata = [];
    private readonly Dictionary<XRMeshRenderer, (AABB Bounds, long BoneGeneration)> _lastPublishedPaletteInputBoundsByRenderer =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRMeshRenderer, (AABB Bounds, long BoneGeneration)> _pendingPaletteInputBoundsByRenderer =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private XRShader? _gpuBoundsShader;
    private XRShader? _gpuBoundsToSceneShader;
    private XRRenderProgram? _gpuBoundsProgram;
    private XRRenderProgram? _gpuBoundsToSceneProgram;
    private XRDataBuffer<PhysicsChainGpuBoundsWorkItem>? _gpuBoundsWorkItemBuffer;
    private XRDataBuffer<PhysicsChainGpuBoundsCopyItem>? _gpuBoundsCopyItemBuffer;
    private XRDataBuffer<uint>? _gpuBoundsAtlasBuffer;
    private XRDataBuffer<uint>? _gpuBoundsSlotMetadataBuffer;
    private int _gpuBoundsDispatchCount;
    private int _gpuBoundsSceneCopyDispatchCount;
    private int _gpuBoundsPublishedCommandCount;
    private int _pendingGpuBoundsCommandCount;
    private long _gpuBoundsMaterialRejectionCount;
    private int _gpuBoundsLastMaterialRejectionCount;
    private long _gpuBoundsCompatibilityRouteRejectionCount;
    private int _gpuBoundsLastCompatibilityRouteRejectionCount;
    private string _gpuBoundsFailureStage = "GpuBoundsPublication";

    /// <summary>Reports the bounds program state without accessing GPU memory.</summary>
    public XRRenderProgram.ShaderProgramBackendStatus? BoundsProgramStatus => _gpuBoundsProgram?.ShaderMetadata.Backend;

    /// <summary>Reports the bounds-copy program state without accessing GPU memory.</summary>
    public XRRenderProgram.ShaderProgramBackendStatus? BoundsCopyProgramStatus => _gpuBoundsToSceneProgram?.ShaderMetadata.Backend;

    /// <summary>Reports bounds input storage state without reading GPU memory.</summary>
    public XRBufferStateSnapshot? BoundsWorkItemBufferStatus => _gpuBoundsWorkItemBuffer?.GetStateSnapshot();

    /// <summary>Reports bounds output storage state without reading GPU memory.</summary>
    public XRBufferStateSnapshot? BoundsAtlasBufferStatus => _gpuBoundsAtlasBuffer?.GetStateSnapshot();

    private static readonly PhysicsChainComputePass BoundsCompletionPass = new(
        PhysicsChainComputePassKind.BoundsPublication,
        EMemoryBarrierMask.ShaderStorage);

    /// <summary>Reports bounds counters as a property for live inspection tools.</summary>
    public PhysicsChainGpuBoundsDiagnostics GpuBoundsDiagnostics => GetGpuBoundsDiagnosticsSnapshot();

    public PhysicsChainGpuBoundsDiagnostics GetGpuBoundsDiagnosticsSnapshot()
        => new(
            _gpuBoundsSlotAllocator.LiveSliceCount,
            _gpuBoundsSlotAllocator.HighWater,
            _gpuBoundsDispatchCount,
            _gpuBoundsSceneCopyDispatchCount,
            _gpuBoundsPublishedCommandCount,
            Interlocked.Read(ref _gpuBoundsMaterialRejectionCount),
            Volatile.Read(ref _gpuBoundsLastMaterialRejectionCount),
            UsesCpuReadback: false,
            CompatibilityRouteRejectionCount: Interlocked.Read(ref _gpuBoundsCompatibilityRouteRejectionCount),
            LastCompatibilityRouteRejectionCount: Volatile.Read(ref _gpuBoundsLastCompatibilityRouteRejectionCount));

    private bool PublishGpuDrivenBounds(
        IPhysicsChainComputeBackend backend,
        IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        _gpuBoundsFailureStage = "GpuBoundsPublication.InputOrThread";
        if (requests.Count == 0 || !RuntimeEngine.IsRenderThread)
            return requests.Count == 0;

        _gpuBoundsFailureStage = "GpuBoundsPublication.Programs";
        EnsureGpuBoundsPrograms();
        if (_gpuBoundsProgram is null || _gpuBoundsToSceneProgram is null)
            return false;
        _gpuBoundsFailureStage = "GpuBoundsPublication.BoundsProgramLink";
        if (!EnsureProgramLinked(_gpuBoundsProgram))
            return false;
        _gpuBoundsFailureStage = "GpuBoundsPublication.CopyProgramLink";
        if (!EnsureProgramLinked(_gpuBoundsToSceneProgram))
            return false;

        if (_writingOutputPageIndex < 0)
            return false;

        _gpuBoundsFailureStage = "GpuBoundsPublication.SceneRoutesUnavailable";
        try
        {
            // The drawn materials and the copy targets come from one route capture.
            return CaptureGpuBoundsSceneRoutes()
                && PublishGpuDrivenBoundsWithRoutes(backend, requests);
        }
        finally
        {
            ReleaseGpuBoundsSceneRoutes();
        }
    }

    private bool PublishGpuDrivenBoundsWithRoutes(
        IPhysicsChainComputeBackend backend,
        IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        if (_gpuBoundsProgram is not { } boundsProgram)
            return false;

        PhysicsChainOutputPage outputPage = _outputPages[_writingOutputPageIndex];
        int materialRejections = 0;
        _gpuBoundsSlotAllocator.BeginLayout();
        _gpuBoundsWorkItems.Clear();
        _gpuBoundsSlotByRenderer.Clear();
        _gpuBoundsRequestByComponent.Clear();
        _pendingBoundsSources.Clear();
        _pendingPaletteInputBoundsByRenderer.Clear();
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            if (request.ParticleOffset >= 0 && request.Particles.Count > 0)
                _gpuBoundsRequestByComponent[request.Component] = request;
        }
        // The palette pass has prepared these bindings and slice bases for all requests.
        if (_gpuDrivenPaletteBindings.Count != _gpuDrivenPaletteSliceBases.Count
            || outputPage.CurrentPalette is null || outputPage.PreviousPalette is null)
        {
            _gpuBoundsFailureStage = "GpuBoundsPublication.PaletteLayout";
            return false;
        }
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            if (!_gpuBoundsRequestByComponent.TryGetValue(binding.Component, out GPUPhysicsChainRequest? request)
                || !IsCertifiedCompleteBoundsBinding(binding)
                || binding.Renderer.BoneBufferGeneration != binding.RendererBoneBufferGeneration)
                continue;

            var key = new PhysicsChainPaletteSliceKey(request.Component, binding.Renderer);
            PhysicsChainPaletteSlice slice = _gpuBoundsSlotAllocator.Acquire(key, 1u);
            uint slotGeneration;
            if (!slice.RequiresHistoryReset
                && _committedBoundsSources.TryGetValue(binding.Renderer, out PhysicsChainGpuBoundsSource prior)
                && prior.BoundsSlot == slice.BaseElement
                && prior.RendererBoneBufferGeneration == binding.RendererBoneBufferGeneration)
                slotGeneration = prior.SlotGeneration;
            else
                slotGeneration = _boundsSlotGenerationOrdinal = checked(_boundsSlotGenerationOrdinal + 1u);

            if (slotGeneration == 0u)
                throw new OverflowException("Physics-chain bounds slot generation was exhausted.");
            var source = new PhysicsChainGpuBoundsSource(this, binding.Renderer, slice.BaseElement,
                slotGeneration, binding.RendererBoneBufferGeneration);
            _pendingBoundsSources.Add(binding.Renderer, source);
            _gpuBoundsSlotByRenderer.Add(binding.Renderer, slice.BaseElement);

            XRMeshRenderer.BlendshapeResourceSnapshot morphState = binding.Renderer.CaptureBlendshapeResources();
            int morphOffset = outputPage.MorphWeights.Count;
            if (!TryRetainMorphWeights(outputPage, binding.Renderer, in morphState)
                || !PhysicsChainMeshEnvelope.TryGetPaletteInputBounds(binding.Renderer,
                    CollectionsMarshal.AsSpan(outputPage.MorphWeights).Slice(morphOffset),
                    morphState.WeightsVersion, out var localBounds, out PhysicsChainMeshEnvelopeStamp envelopeStamp)
                || !localBounds.IsValid
                || !IsFiniteBoundsVector(localBounds.Min)
                || !IsFiniteBoundsVector(localBounds.Max))
            {
                _gpuBoundsFailureStage = "GpuBoundsPublication.MeshEnvelope";
                return false;
            }

            _pendingPaletteInputBoundsByRenderer.Add(binding.Renderer,
                (localBounds, binding.RendererBoneBufferGeneration));
            if (_lastPublishedPaletteInputBoundsByRenderer.TryGetValue(binding.Renderer, out var priorBounds)
                && priorBounds.BoneGeneration == binding.RendererBoneBufferGeneration)
                localBounds = new AABB(Vector3.Min(localBounds.Min, priorBounds.Bounds.Min),
                    Vector3.Max(localBounds.Max, priorBounds.Bounds.Max));

            PhysicsChainMaterialBoundsContract materialContract = ResolveDrawnMaterialBoundsContract(binding);
            uint flags = 0u;
            float padding = materialContract.Padding;
            if (!materialContract.IsSupported)
            {
                // The shader writes a rejected slot; no CPU bound replaces it.
                flags |= BoundsWorkItemRejectedMaterialContract;
                padding = 0.0f;
                ++materialRejections;
                ReportMaterialBoundsRejection(binding.Renderer, materialContract);
            }

            uint paletteBase = _gpuDrivenPaletteSliceBases[bindingIndex];
            uint paletteCount = binding.BoneMatrixElementCount;
            if (paletteCount <= 1u
                || paletteBase >= outputPage.CurrentPalette.ElementCount
                || paletteCount > outputPage.CurrentPalette.ElementCount - paletteBase
                || paletteBase >= outputPage.PreviousPalette.ElementCount
                || paletteCount > outputPage.PreviousPalette.ElementCount - paletteBase)
            {
                _gpuBoundsFailureStage = "GpuBoundsPublication.PaletteSlice";
                return false;
            }
            _gpuBoundsWorkItems.Add(new PhysicsChainGpuBoundsWorkItem(
                new Vector4(localBounds.Min, padding),
                new Vector4(localBounds.Max, 0f),
                paletteBase,
                paletteCount,
                slice.BaseElement,
                flags));
            PhysicsChainOutputPage? priorPage = _publishedOutputPageIndex >= 0
                ? _outputPages[_publishedOutputPageIndex] : null;
            bool spatialValid = TryBuildRendererSpatialBounds(outputPage, request, in binding,
                in envelopeStamp, in materialContract, priorPage, out _, out var currentSpatial,
                out var previousSpatial, out AABB spatialBounds);
            AABB committedBounds = spatialBounds;
            ulong committedBoundsVersion = 0UL;
            if (spatialValid)
                ResolveCommittedSpatialProxy(priorPage, binding.Renderer, in source, in spatialBounds,
                    out committedBounds, out committedBoundsVersion);
            outputPage.RendererStates.Add(binding.Renderer, new(source, paletteBase,
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
                MaterialRouteScene = binding.Component.World.GetRenderWorld()?.VisualScene.GPUCommands,
            });
            bool hasTightCheckpoint = request.ResidentSolveCompleted || !request.ResidentSpatialValid;
            outputPage.SpatialCheckpoints[request] = new(request.ResidentSpatialRevision,
                hasTightCheckpoint
                    ? new(request.ResidentTightParticleBounds, request.ResidentTightBasisStretch)
                    : currentSpatial,
                hasTightCheckpoint ? request.ResidentTightSpatialValid : request.ResidentSpatialValid);
        }
        _gpuBoundsSlotAllocator.EndLayout();
        Volatile.Write(ref _gpuBoundsLastMaterialRejectionCount, materialRejections);
        if (materialRejections != 0)
            Interlocked.Add(ref _gpuBoundsMaterialRejectionCount, materialRejections);

        if (_gpuBoundsWorkItems.Count == 0)
        {
            _gpuBoundsFailureStage = "GpuBoundsPublication.NoCertifiedBounds";
            return false;
        }

        bool workItemsResized = EnsureBufferCapacity(
            ref _gpuBoundsWorkItemBuffer,
            "PhysicsChainGlobalBoundsWorkItems",
            checked((uint)_gpuBoundsWorkItems.Count));
        uint boundsElementCount = checked(Math.Max(_gpuBoundsSlotAllocator.HighWater, 1u) * 8u);
        if (outputPage.BoundsAtlas is null)
        {
            // Each live slot is written by the bounds pass before it is consumed.
            outputPage.BoundsAtlas = new XRDataBuffer<uint>(
                "PhysicsChainGlobalBoundsAtlas", EBufferTarget.ShaderStorageBuffer,
                XRMath.NextPowerOfTwo(boundsElementCount), allocateClientSideSource: false)
            {
                GpuProduced = true,
                DefaultMemoryPolicy = XRBufferMemoryPolicy.GpuOnly,
                DisposeOnPush = false,
                Usage = EBufferUsage.StaticCopy,
            };
        }
        else if (outputPage.BoundsAtlas.ElementCount < boundsElementCount)
            outputPage.BoundsAtlas.Resize(XRMath.NextPowerOfTwo(boundsElementCount), copyData: false);
        _gpuBoundsAtlasBuffer = outputPage.BoundsAtlas;

        int metadataWordCount = checked((int)Math.Max(_gpuBoundsSlotAllocator.HighWater, 1u) * 4);
        while (_gpuBoundsSlotMetadata.Count < metadataWordCount)
            _gpuBoundsSlotMetadata.Add(0u);
        for (int index = 0; index < _gpuBoundsSlotMetadata.Count; ++index)
            _gpuBoundsSlotMetadata[index] = 0u;
        foreach (PhysicsChainGpuBoundsSource source in _pendingBoundsSources.Values)
        {
            int index = checked((int)source.BoundsSlot * 4);
            _gpuBoundsSlotMetadata[index] = source.SlotGeneration;
            _gpuBoundsSlotMetadata[index + 1] = checked(_outputProducerEpoch + 1u);
            _gpuBoundsSlotMetadata[index + 2] = unchecked((uint)source.RendererBoneBufferGeneration);
            _gpuBoundsSlotMetadata[index + 3] = unchecked((uint)(source.RendererBoneBufferGeneration >> 32));
        }
        bool metadataResized = EnsureBufferCapacity(ref outputPage.SlotMetadata,
            "PhysicsChainGlobalBoundsSlotMetadata", checked((uint)metadataWordCount));
        _gpuBoundsSlotMetadataBuffer = outputPage.SlotMetadata;
        uint metadataBytes = _gpuBoundsSlotMetadataBuffer?.WriteDataRaw(
            CollectionsMarshal.AsSpan(_gpuBoundsSlotMetadata).Slice(0, metadataWordCount)) ?? 0u;
        PushBufferUpdate(_gpuBoundsSlotMetadataBuffer, metadataResized, metadataBytes);
        _gpuBoundsFailureStage = "GpuBoundsPublication.Buffers";
        if (_gpuBoundsWorkItemBuffer is null || _gpuBoundsAtlasBuffer is null || _gpuBoundsSlotMetadataBuffer is null)
            return false;

        uint workItemBytes = _gpuBoundsWorkItemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuBoundsWorkItems));
        PushBufferUpdate(_gpuBoundsWorkItemBuffer, workItemsResized, workItemBytes);
        RecordCpuUploadBytes(
            workItemsResized ? _gpuBoundsWorkItemBuffer.Length : workItemBytes,
            _currentDispatchGroupIsBatched);
        _gpuBoundsFailureStage = "GpuBoundsPublication.PaletteBufferReadiness";
        if (!backend.EnsureGpuBufferReady(outputPage.CurrentPalette)
            || !backend.EnsureGpuBufferReady(outputPage.PreviousPalette))
            return false;
        _gpuBoundsFailureStage = "GpuBoundsPublication.WorkItemBufferReadiness";
        if (!backend.EnsureGpuBufferReady(_gpuBoundsWorkItemBuffer))
            return false;
        _gpuBoundsFailureStage = "GpuBoundsPublication.AtlasBufferReadiness";
        if (!backend.EnsureGpuBufferReady(_gpuBoundsAtlasBuffer))
            return false;
        if (!backend.EnsureGpuBufferReady(_gpuBoundsSlotMetadataBuffer))
            return false;

        boundsProgram.Uniform("WorkItemCount", checked((uint)_gpuBoundsWorkItems.Count));
        boundsProgram.BindBuffer(_gpuBoundsWorkItemBuffer, 0);
        boundsProgram.BindBuffer(outputPage.CurrentPalette, 1);
        boundsProgram.BindBuffer(outputPage.PreviousPalette, 2);
        boundsProgram.BindBuffer(_gpuBoundsAtlasBuffer, 3);
        _gpuBoundsFailureStage = "GpuBoundsPublication.Dispatch";
        outputPage.HasQueuedGpuWork = true;
        if (!TryDispatchDirect(
                backend,
                boundsProgram,
                checked((uint)_gpuBoundsWorkItems.Count),
                1u,
                1u,
                PhysicsChainComputePassKind.BoundsPublication))
            return false;
        _gpuBoundsFailureStage = "GpuBoundsPublication.Barrier";
        if (!TryCompletePass(backend, BoundsCompletionPass))
            return false;
        ++_gpuBoundsDispatchCount;

        _gpuBoundsCopiedRenderers.Clear();
        _pendingGpuBoundsCommandCount = 0;
        Volatile.Write(ref _gpuBoundsLastCompatibilityRouteRejectionCount, 0);
        _gpuBoundsFailureStage = "GpuBoundsPublication.SceneCopy";
        for (int sceneIndex = 0; sceneIndex < _gpuBoundsScenes.Count; ++sceneIndex)
            if (!PublishGpuBoundsToScene(backend, sceneIndex))
                return false;
        return true;
    }

    private static bool TryRetainMorphWeights(PhysicsChainOutputPage page, XRMeshRenderer renderer,
        in XRMeshRenderer.BlendshapeResourceSnapshot source)
    {
        int count = source.ActiveCount;
        if (count == 0)
            return true;
        if (count < 0 || renderer.Mesh is not { } mesh || source.ActiveWeights is not { } weights ||
            weights.ClientSideSource is null || (uint)count > weights.ElementCount)
            return false;
        for (uint index = 0; index < (uint)count; ++index)
        {
            Vector2 value = weights.GetVector2(index);
            if (!float.IsFinite(value.X) || value.X < 0.0f || value.X >= mesh.BlendshapeCount ||
                value.X != MathF.Truncate(value.X) || !float.IsFinite(value.Y))
                return false;
            page.MorphWeights.Add(new((uint)value.X, value.Y));
        }
        return true;
    }

    /// <summary>
    /// Collects the scenes of all palette bindings and captures each scene's
    /// renderer routes once. Returns false when a scene has no published routes.
    /// </summary>
    private bool CaptureGpuBoundsSceneRoutes()
    {
        _gpuBoundsScenes.Clear();
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            if (binding.Component.World.GetRenderWorld() is not { } renderWorld)
                continue;

            GPUScene scene = renderWorld.VisualScene.GPUCommands;
            if (!_gpuBoundsScenes.Contains(scene))
                _gpuBoundsScenes.Add(scene);
        }

        for (int sceneIndex = 0; sceneIndex < _gpuBoundsScenes.Count; ++sceneIndex)
        {
            if (sceneIndex == _gpuBoundsSceneRoutes.Count)
                _gpuBoundsSceneRoutes.Add(new GpuSceneRendererCommandIndexSnapshot());

            // Read the renderer routes from the command set published at scene swap.
            GpuSceneRendererCommandIndexSnapshot routes = _gpuBoundsSceneRoutes[sceneIndex];
            _gpuBoundsScenes[sceneIndex].CaptureRendererCommandIndices(routes);
            if (routes.PublicationGeneration < 0)
                return false;
        }

        return true;
    }

    private void ReleaseGpuBoundsSceneRoutes()
    {
        for (int index = 0; index < _gpuBoundsSceneRoutes.Count; ++index)
            _gpuBoundsSceneRoutes[index].Clear();
        _gpuBoundsMaterialScratch.Clear();
    }

    private GpuSceneRendererCommandIndexSnapshot? FindGpuBoundsSceneRoutes(in GpuDrivenRendererPaletteBinding binding)
    {
        if (binding.Component.World.GetRenderWorld() is not { } renderWorld)
            return null;

        GPUScene scene = renderWorld.VisualScene.GPUCommands;
        for (int sceneIndex = 0; sceneIndex < _gpuBoundsScenes.Count; ++sceneIndex)
            if (ReferenceEquals(_gpuBoundsScenes[sceneIndex], scene))
                return _gpuBoundsSceneRoutes[sceneIndex];
        return null;
    }

    /// <summary>
    /// Combines the vertex-effect contracts of every material that draws this
    /// renderer's bound. Published routes supply the drawn materials, including
    /// command overrides. A renderer without a route uses its own materials.
    /// </summary>
    private PhysicsChainMaterialBoundsContract ResolveDrawnMaterialBoundsContract(
        in GpuDrivenRendererPaletteBinding binding)
    {
        XRMeshRenderer renderer = binding.Renderer;
        PhysicsChainMaterialBoundsContract contract = default;
        if (FindGpuBoundsSceneRoutes(binding) is { } routes
            && routes.TryGetCommandMaterialSnapshots(renderer, _gpuBoundsMaterialScratch))
        {
            for (int index = 0; index < _gpuBoundsMaterialScratch.Count; ++index)
            {
                PhysicsChainDrawMaterialSnapshot drawn = _gpuBoundsMaterialScratch[index];
                if (!drawn.IsCurrent)
                {
                    _gpuBoundsMaterialScratch.Clear();
                    return new(0.0f, PhysicsChainMaterialBoundsEvaluator.StalePublishedMaterialRejection);
                }
                contract = PhysicsChainMaterialBoundsContract.Combine(contract,
                    PhysicsChainMaterialBoundsEvaluator.Evaluate(drawn.Material));
            }
            _gpuBoundsMaterialScratch.Clear();
            return contract;
        }

        for (int primitive = 0; renderer.TryGetMesh(primitive, out _, out XRMaterial? material); ++primitive)
            contract = PhysicsChainMaterialBoundsContract.Combine(contract,
                PhysicsChainMaterialBoundsEvaluator.Evaluate(material));
        return contract;
    }

    private static void ReportMaterialBoundsRejection(
        XRMeshRenderer renderer,
        in PhysicsChainMaterialBoundsContract contract)
    {
        const string LogKey = "GPUPhysicsChainDispatcher.Bounds.MaterialContractRejected";
        if (!XREngine.Debug.ShouldLogEvery(LogKey, TimeSpan.FromSeconds(5.0)))
            return;

        XREngine.Debug.PhysicsWarning(
            $"[GPUPhysicsChainDispatcher] GPU bounds rejected for renderer '{renderer.Mesh?.Name ?? "<unnamed>"}': " +
            $"{contract.RejectionReason}. The draw has no valid culling bound; no CPU bound was substituted.");
    }

    private bool PublishGpuBoundsToScene(IPhysicsChainComputeBackend backend, int sceneIndex)
    {
        GPUScene scene = _gpuBoundsScenes[sceneIndex];
        GpuSceneRendererCommandIndexSnapshot routes = _gpuBoundsSceneRoutes[sceneIndex];
        // A scene swap can replace a command route after the palette bound was made.
        scene.CaptureRendererCommandIndices(routes);
        if (routes.PublicationGeneration < 0)
            return false;
        _gpuBoundsCopyItems.Clear();
        uint maximumCommandSlot = 0u;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            if (binding.Component.World.GetRenderWorld() is not { } renderWorld
                || !ReferenceEquals(renderWorld.VisualScene.GPUCommands, scene)
                || !_gpuBoundsSlotByRenderer.ContainsKey(binding.Renderer)
                || !routes.TryGetCommandIndices(binding.Renderer, _gpuBoundsCommandScratch))
                continue;

            for (int commandIndex = 0; commandIndex < _gpuBoundsCommandScratch.Count; ++commandIndex)
            {
                uint targetSlot = _gpuBoundsCommandScratch[commandIndex];
                // Compatibility culling has no retained page and source lease for this slot.
                _gpuBoundsCopyItems.Add(new PhysicsChainGpuBoundsCopyItem(uint.MaxValue, targetSlot));
                maximumCommandSlot = Math.Max(maximumCommandSlot, targetSlot);
            }

            if (_gpuBoundsCommandScratch.Count > 0)
                _gpuBoundsCopiedRenderers.Add(binding.Renderer);
        }

        if (_gpuBoundsCopyItems.Count == 0)
            return true;

        scene.EnsureCommandAabbCapacity(maximumCommandSlot + 1u);
        scene.EnsureGpuCullBoundsCapacity(maximumCommandSlot + 1u);
        XRDataBuffer? sceneBoundsBuffer = scene.CommandAabbBuffer;
        XRDataBuffer sceneCullBoundsBuffer = scene.CullBoundsBuffer;
        if (sceneBoundsBuffer is null || _gpuBoundsAtlasBuffer is null)
            return false;

        bool copyItemsResized = EnsureBufferCapacity(
            ref _gpuBoundsCopyItemBuffer,
            "PhysicsChainGlobalBoundsCopyItems",
            checked((uint)_gpuBoundsCopyItems.Count));
        if (_gpuBoundsCopyItemBuffer is null)
            return false;

        uint copyItemBytes = _gpuBoundsCopyItemBuffer.WriteDataRaw(CollectionsMarshal.AsSpan(_gpuBoundsCopyItems));
        PushBufferUpdate(_gpuBoundsCopyItemBuffer, copyItemsResized, copyItemBytes);
        RecordCpuUploadBytes(
            copyItemsResized ? _gpuBoundsCopyItemBuffer.Length : copyItemBytes,
            _currentDispatchGroupIsBatched);
        if (!backend.EnsureGpuBufferReady(_gpuBoundsAtlasBuffer)
            || !backend.EnsureGpuBufferReady(sceneBoundsBuffer)
            || !backend.EnsureGpuBufferReady(sceneCullBoundsBuffer)
            || !backend.EnsureGpuBufferReady(_gpuBoundsCopyItemBuffer))
            return false;

        _gpuBoundsToSceneProgram!.Uniform("CopyItemCount", checked((uint)_gpuBoundsCopyItems.Count));
        _gpuBoundsToSceneProgram.Uniform("BoundsVersion", checked(_outputProducerEpoch + 1u));
        _gpuBoundsToSceneProgram.BindBuffer(_gpuBoundsAtlasBuffer, 0);
        _gpuBoundsToSceneProgram.BindBuffer(sceneBoundsBuffer, 1);
        _gpuBoundsToSceneProgram.BindBuffer(_gpuBoundsCopyItemBuffer, 2);
        _gpuBoundsToSceneProgram.BindBuffer(sceneCullBoundsBuffer, 3);
        uint groupCount = (checked((uint)_gpuBoundsCopyItems.Count) + BoundsCopyWorkgroupSize - 1u) / BoundsCopyWorkgroupSize;
        if (!TryDispatchDirect(
                backend,
                _gpuBoundsToSceneProgram,
                Math.Max(groupCount, 1u),
                1u,
                1u,
                PhysicsChainComputePassKind.BoundsPublication))
            return false;
        if (!TryCompletePass(backend, BoundsCompletionPass))
            return false;
        ++_gpuBoundsSceneCopyDispatchCount;
        _pendingGpuBoundsCommandCount += _gpuBoundsCopyItems.Count;
        Interlocked.Add(ref _gpuBoundsCompatibilityRouteRejectionCount, _gpuBoundsCopyItems.Count);
        Volatile.Write(ref _gpuBoundsLastCompatibilityRouteRejectionCount,
            Volatile.Read(ref _gpuBoundsLastCompatibilityRouteRejectionCount) + _gpuBoundsCopyItems.Count);
        ReportCompatibilityRouteRejection(_gpuBoundsCopyItems.Count);
        return true;
    }

    private static void ReportCompatibilityRouteRejection(int commandCount)
    {
        const string LogKey = "GPUPhysicsChainDispatcher.Bounds.CompatibilityRouteRejected";
        if (!XREngine.Debug.ShouldLogEvery(LogKey, TimeSpan.FromSeconds(5.0)))
            return;

        XREngine.Debug.PhysicsWarning(
            $"[GPUPhysicsChainDispatcher] GPUScene compatibility culling rejected bounds for {commandCount} covered chain commands: " +
            "the draw has no retained physics output page and source identity. No CPU bound was substituted.");
    }

    private void FinalizeGpuBoundsSceneOwnership()
    {
        _gpuBoundsPublishedCommandCount += _pendingGpuBoundsCommandCount;
        _pendingGpuBoundsCommandCount = 0;
        for (int sceneIndex = 0; sceneIndex < _gpuBoundsScenes.Count; ++sceneIndex)
        {
            GPUScene scene = _gpuBoundsScenes[sceneIndex];
            bool published = false;
            for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
            {
                GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
                if (!_gpuBoundsCopiedRenderers.Contains(binding.Renderer)
                    || binding.Component.World.GetRenderWorld() is not { } renderWorld
                    || !ReferenceEquals(renderWorld.VisualScene.GPUCommands, scene))
                    continue;

                scene.SetRendererOwnsGpuCopiedAabb(binding.Renderer, true);
                published = true;
            }

            if (published)
                scene.MarkGpuCommandBoundsPublished();
        }
    }

    private void EnsureGpuBoundsPrograms()
    {
        if (_gpuBoundsProgram is null)
        {
            _gpuBoundsShader = ShaderHelper.LoadEngineShader(
                "Compute/PhysicsChain/PhysicsChainBounds.comp",
                EShaderType.Compute);
            _gpuBoundsProgram = new XRRenderProgram(true, false, _gpuBoundsShader);
        }

        if (_gpuBoundsToSceneProgram is null)
        {
            _gpuBoundsToSceneShader = ShaderHelper.LoadEngineShader(
                "Compute/PhysicsChain/PhysicsChainBoundsToScene.comp",
                EShaderType.Compute);
            _gpuBoundsToSceneProgram = new XRRenderProgram(true, false, _gpuBoundsToSceneShader);
        }
    }

    private static bool EnsureProgramLinked(XRRenderProgram program)
    {
        if (program.IsLinked)
            return true;
        program.Link();
        return program.IsLinked;
    }

    private static bool IsFiniteBoundsVector(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void ResetGpuBoundsResources()
    {
        _gpuBoundsSlotAllocator.Reset();
        _gpuBoundsWorkItems.Clear();
        _gpuBoundsCopyItems.Clear();
        _gpuBoundsCommandScratch.Clear();
        ReleaseGpuBoundsSceneRoutes();
        _gpuBoundsScenes.Clear();
        _gpuBoundsCopiedRenderers.Clear();
        _pendingGpuBoundsCommandCount = 0;
        _gpuBoundsSlotByRenderer.Clear();
        _gpuBoundsRequestByComponent.Clear();
        _gpuBoundsSlotMetadata.Clear();
        _pendingBoundsSources.Clear();
        _committedBoundsSources.Clear();
        _pendingPaletteInputBoundsByRenderer.Clear();
        _lastPublishedPaletteInputBoundsByRenderer.Clear();
    }

    /// <summary>
    /// One 48-byte bounds work item. <c>LocalMinimum.W</c> holds the world-space
    /// material vertex-effect padding. <c>Flags</c> holds
    /// <see cref="BoundsWorkItemRejectedMaterialContract"/> when the drawn
    /// materials reject the contract.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PhysicsChainGpuBoundsWorkItem(
        Vector4 LocalMinimum,
        Vector4 LocalMaximum,
        uint PaletteBase,
        uint PaletteCount,
        uint BoundsSlot,
        uint Flags);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PhysicsChainGpuBoundsCopyItem(
        uint SourceSlot,
        uint TargetCommandSlot);

    private void DisposeGpuBoundsResources()
    {
        _gpuBoundsWorkItemBuffer?.Dispose();
        _gpuBoundsCopyItemBuffer?.Dispose();
        DisposeOutputPages();
        _gpuBoundsProgram?.Destroy();
        _gpuBoundsToSceneProgram?.Destroy();
        _gpuBoundsShader?.Destroy();
        _gpuBoundsToSceneShader?.Destroy();
        _gpuBoundsWorkItemBuffer = null;
        _gpuBoundsCopyItemBuffer = null;
        _gpuBoundsAtlasBuffer = null;
        _gpuBoundsSlotMetadataBuffer = null;
        _gpuBoundsProgram = null;
        _gpuBoundsToSceneProgram = null;
        _gpuBoundsShader = null;
        _gpuBoundsToSceneShader = null;
        _gpuBoundsDispatchCount = 0;
        _gpuBoundsSceneCopyDispatchCount = 0;
        _gpuBoundsPublishedCommandCount = 0;
    }
}

/// <summary>Physics-chain GPU bounds counters, read without GPU memory access.</summary>
/// <param name="MaterialContractRejectionCount">Total bounds slots rejected by a drawn material contract.</param>
/// <param name="LastMaterialContractRejectionCount">Slots rejected by a material contract in the last publication.</param>
/// <param name="CompatibilityRouteRejectionCount">Total GPUScene compatibility command copies rejected without a retained physics source.</param>
/// <param name="LastCompatibilityRouteRejectionCount">Compatibility command copies rejected in the last bounds publication.</param>
public readonly record struct PhysicsChainGpuBoundsDiagnostics(
    int LiveSlotCount,
    uint SlotHighWater,
    int BoundsDispatchCount,
    int SceneCopyDispatchCount,
    int PublishedCommandCount,
    long MaterialContractRejectionCount,
    int LastMaterialContractRejectionCount,
    bool UsesCpuReadback,
    long CompatibilityRouteRejectionCount,
    int LastCompatibilityRouteRejectionCount);
