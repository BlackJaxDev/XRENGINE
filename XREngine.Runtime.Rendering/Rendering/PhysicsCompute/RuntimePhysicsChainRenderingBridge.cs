using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Transforms;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Rendering-side installation point for physics-chain graphics services.  The
/// Core assembly deliberately knows only <see cref="IRuntimePhysicsChainRenderingBridge"/>.
/// </summary>
public sealed class RuntimePhysicsChainRenderingBridge : IRuntimePhysicsChainRenderingBridge
{
    private readonly Dictionary<PhysicsChainComponent, Source> _sources = [];
    private static readonly ConditionalWeakTable<PhysicsChainWorld, PhysicsChainReadbackCoordinatorAdapter> ReadbackCoordinators = new();
    public static RuntimePhysicsChainRenderingBridge Instance { get; } = new();

    private RuntimePhysicsChainRenderingBridge() { }

    public PhysicsChainGpuBackendState BackendState
        => (PhysicsChainGpuBackendState)GPUPhysicsChainDispatcher.Instance.BackendStatus.State;

    /// <summary>Reads chain bindings and the last captured frame counters.</summary>
    public PhysicsChainRenderingDiagnostics GetDiagnostics(PhysicsChainComponent chain)
    {
        _sources.TryGetValue(chain, out Source? source);
        AdvancedPreparationDiagnosticSnapshot? preparation =
            AdvancedSharedPreparationService.GetCurrentDiagnostics();
        AdvancedPreparationPublication publication = preparation?.Publication ?? default;
        GPUPhysicsChainDispatcher dispatcher = GPUPhysicsChainDispatcher.Instance;
        GPUPhysicsChainBackendStatus backend = dispatcher.BackendStatus;
        PhysicsChainDispatchDiagnostics dispatch = dispatcher.DispatchDiagnostics;
        PhysicsChainInputPageDiagnostics inputPages = dispatcher.InputPageDiagnostics;
        string? lastError = backend.State != GPUPhysicsChainBackendState.Ready
            ? backend.Diagnostic
            : dispatch.FailureCount > 0L
                ? dispatch.LastFailureStage
                : inputPages.FailureCount > 0L
                    ? inputPages.LastFailure
                    : null;

        return new PhysicsChainRenderingDiagnostics(
            source?.BoundRendererCount ?? 0,
            RuntimeEngine.Rendering.Stats.SceneAssets.VisibleRendererCount,
            publication.DeformationDispatchCount,
            RuntimeEngine.Rendering.Stats.SceneAssets.SkinningComputeDispatchCount,
            publication.GpuResourcesPublished && publication.IndirectRangeCount > 0u
                ? publication.DrawCount
                : 0u,
            publication.Backend == RuntimeGraphicsApiKind.Vulkan,
            publication.Backend == RuntimeGraphicsApiKind.Vulkan
                ? RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndirectSubmittedDraws
                : 0L,
            RuntimeEngine.Rendering.Stats.RendererState.GpuIndirectDrawCalls,
            RuntimeEngine.Rendering.Stats.Frame.DrawCalls,
            RuntimeEngine.Rendering.Stats.SceneAssets.VisibleTriangleCount,
            source?.GetConservativeBoundsStatus(),
            lastError);
    }

    /// <summary>Installs the renderer implementation after the rendering runtime has initialized.</summary>
    public static IDisposable Install()
        => RuntimePhysicsChainRendering.Install(Instance);

    public void Register(PhysicsChainComponent chain)
    {
        if (_sources.TryGetValue(chain, out Source? existing))
        {
            existing.UpdateReadbackCoordinator();
            if (!GPUPhysicsChainDispatcher.Instance.IsRegistered(existing))
                GPUPhysicsChainDispatcher.Instance.Register(existing);
            return;
        }

        Source source = new(chain);
        source.UpdateReadbackCoordinator();
        _sources.Add(chain, source);
        GPUPhysicsChainDispatcher.Instance.Register(source);
    }

    /// <summary>Gets the rendering-owned compute adapter for a registered Core chain.</summary>
    public bool TryGetComputeSource(PhysicsChainComponent chain, out IPhysicsChainComputeSource? source)
    {
        if (_sources.TryGetValue(chain, out Source? resolved))
        {
            source = resolved;
            return true;
        }

        source = null;
        return false;
    }

    /// <summary>Registers a Core chain when needed and returns its rendering-owned compute adapter.</summary>
    public IPhysicsChainComputeSource GetOrCreateComputeSource(PhysicsChainComponent chain)
    {
        Register(chain);
        return _sources[chain];
    }

    public void Unregister(PhysicsChainComponent chain)
    {
        if (!_sources.Remove(chain, out Source? source))
            return;

        GPUPhysicsChainDispatcher.Instance.Unregister(source);
        source.ReleaseReadbackCoordinator();
        source.ReleasePaletteBindings();
    }

    public void Execute(PhysicsChainComponent chain, in PhysicsChainGpuDispatchSnapshot snapshot)
    {
        if (!_sources.TryGetValue(chain, out Source? source))
            return;

        source.UpdateReadbackCoordinator();
        if (source.DebugDrawChains)
            GPUPhysicsChainDispatcher.Instance.RefreshDebugDrawChainsWorld(source);
        GPUPhysicsChainDispatcher.Instance.SubmitData(source, snapshot,
            chain.UseGpuDrivenSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning &&
                RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader);
    }

    public void RenderDebug(PhysicsChainComponent chain)
        => GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug(chain.World);

    public void SetDebugDrawChains(PhysicsChainComponent chain, bool selected)
    {
        if (_sources.TryGetValue(chain, out Source? source))
            source.SetDebugDrawChains(selected);
    }

    public void NotifyReadbackUnavailable(PhysicsChainComponent chain, string reason)
    {
        // Core already records the compatibility fault; this keeps renderer-side
        // diagnostics on the same explicit unavailable path.
    }

    public void InvalidateGpuDrivenRenderers(PhysicsChainComponent chain)
    {
        if (_sources.TryGetValue(chain, out Source? source))
            source.InvalidateGpuDrivenRenderers();
    }

    public void RecordHierarchyRecalculationTicks(long ticks)
        => GPUPhysicsChainDispatcher.RecordHierarchyRecalcTicks(ticks);

    private sealed class Source(PhysicsChainComponent chain) : IPhysicsChainComputeSource
    {
        private readonly PhysicsChainComponent _chain = chain;
        private readonly List<PhysicsChainGpuParticle> _readback = [];
        private readonly List<PhysicsChainGpuBone> _bones = [];
        private readonly Dictionary<Transform, int> _particleIndices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, int> _firstChildren = [];
        private readonly List<PaletteState> _paletteStates = [];
        private int _copiedBoneStructureSignature;
        private int _bindingGeneration;
        private volatile bool _paletteBindingsDirty;
        private int _bindingInvalidationRevision;
        private int _bindingAppliedRevision;
        private volatile bool _paletteBindingRetryPending;
        private XRMeshRenderer? _paletteRetryRenderer;
        private long _paletteRetryGeneration;
        private bool _paletteRetryAttempted;
        private int _particleStateVersion;
        private volatile bool _debugDrawChains = chain.DebugDrawChains;
        private bool _effectiveGpuMode;
        private PhysicsChainReadbackCoordinatorAdapter? _readbackCoordinator;
        private IRuntimeWorldContext? _readbackRuntimeWorld;

        public Guid ID => _chain.ID;
        public IRuntimeWorldContext? World => _chain.World;
        public PhysicsChainRuntimeHandle RuntimeHandle => _chain.RuntimeHandle;
        public long ReadbackSourceGeneration => _chain.ReadbackSourceGeneration;
        public IPhysicsChainReadbackCoordinator? ReadbackCoordinator
            => Volatile.Read(ref _readbackCoordinator);

        public void UpdateReadbackCoordinator()
        {
            IRuntimeWorldContext? runtimeWorld = _chain.World;
            if (ReferenceEquals(runtimeWorld, _readbackRuntimeWorld))
                return;

            PhysicsChainReadbackCoordinatorAdapter? next = null;
            if (runtimeWorld is not null &&
                PhysicsChainWorld.TryGet(runtimeWorld, out PhysicsChainWorld? world) &&
                world is not null)
                next = ReadbackCoordinators.GetValue(world, static key => new(key));

            if (runtimeWorld is not null && next is null)
                return;

            PhysicsChainReadbackCoordinatorAdapter? previous = _readbackCoordinator;
            if (!ReferenceEquals(previous, next))
            {
                next?.AddSource();
                Volatile.Write(ref _readbackCoordinator, next);
                previous?.RemoveSource();
            }
            _readbackRuntimeWorld = runtimeWorld;
        }

        public void ReleaseReadbackCoordinator()
        {
            PhysicsChainReadbackCoordinatorAdapter? previous = Interlocked.Exchange(ref _readbackCoordinator, null);
            previous?.RemoveSource();
            _readbackRuntimeWorld = null;
        }
        public int UpdateMode => (int)_chain.UpdateMode;
        public bool UseBatchedDispatcher => _chain.UseBatchedDispatcher;
        public bool HasGpuDrivenRenderers => _paletteStates.Count > 0;
        public int BoundRendererCount => _paletteStates.Count;

        public string? GetConservativeBoundsStatus()
        {
            if (_paletteStates.Count == 0)
                return null;

            GPUPhysicsChainDispatcher dispatcher = GPUPhysicsChainDispatcher.Instance;
            EPhysicsChainSpatialBoundsStatus status = EPhysicsChainSpatialBoundsStatus.Ready;
            for (int index = 0; index < _paletteStates.Count; ++index)
            {
                status = dispatcher.GetCommittedSpatialBoundsStatus(_paletteStates[index].Renderer);
                if (status != EPhysicsChainSpatialBoundsStatus.Ready)
                    break;
            }
            return status switch
            {
                EPhysicsChainSpatialBoundsStatus.Ready => nameof(EPhysicsChainSpatialBoundsStatus.Ready),
                EPhysicsChainSpatialBoundsStatus.NoPublishedOutput => nameof(EPhysicsChainSpatialBoundsStatus.NoPublishedOutput),
                EPhysicsChainSpatialBoundsStatus.IncompleteBoneCoverage => nameof(EPhysicsChainSpatialBoundsStatus.IncompleteBoneCoverage),
                EPhysicsChainSpatialBoundsStatus.MissingBoundsSource => nameof(EPhysicsChainSpatialBoundsStatus.MissingBoundsSource),
                EPhysicsChainSpatialBoundsStatus.UnavailableProducer => nameof(EPhysicsChainSpatialBoundsStatus.UnavailableProducer),
                EPhysicsChainSpatialBoundsStatus.MissingRendererState => nameof(EPhysicsChainSpatialBoundsStatus.MissingRendererState),
                EPhysicsChainSpatialBoundsStatus.BoundsSourceChanged => nameof(EPhysicsChainSpatialBoundsStatus.BoundsSourceChanged),
                EPhysicsChainSpatialBoundsStatus.InvalidSpatialBounds => nameof(EPhysicsChainSpatialBoundsStatus.InvalidSpatialBounds),
                EPhysicsChainSpatialBoundsStatus.MissingMesh => nameof(EPhysicsChainSpatialBoundsStatus.MissingMesh),
                EPhysicsChainSpatialBoundsStatus.MaterialRouteChanged => nameof(EPhysicsChainSpatialBoundsStatus.MaterialRouteChanged),
                EPhysicsChainSpatialBoundsStatus.MeshEnvelopeChanged => nameof(EPhysicsChainSpatialBoundsStatus.MeshEnvelopeChanged),
                EPhysicsChainSpatialBoundsStatus.BoneGenerationChanged => nameof(EPhysicsChainSpatialBoundsStatus.BoneGenerationChanged),
                EPhysicsChainSpatialBoundsStatus.UnsupportedMaterial => nameof(EPhysicsChainSpatialBoundsStatus.UnsupportedMaterial),
                EPhysicsChainSpatialBoundsStatus.MaterialPaddingChanged => nameof(EPhysicsChainSpatialBoundsStatus.MaterialPaddingChanged),
                EPhysicsChainSpatialBoundsStatus.StaleMaterialSnapshot => nameof(EPhysicsChainSpatialBoundsStatus.StaleMaterialSnapshot),
                _ => "Unknown",
            };
        }
        public bool DebugDrawChains => _debugDrawChains;

        public void SetDebugDrawChains(bool selected)
        {
            _debugDrawChains = selected;
            GPUPhysicsChainDispatcher.Instance.SetDebugDrawChainsSelection(this, selected);
        }
        public int GpuDebugInterpolationMode => (int)_chain.InterpolationMode;
        public float GetGpuDebugInterpolationAlpha() => 0.0f;
        public bool RequiresGpuReadback() => _chain.RequiresGpuReadback();
        public void NotifyGpuReadbackUnavailable(string reason) => _chain.NotifyGpuReadbackUnavailable(reason);
        public void ApplyReadbackData(ReadOnlySpan<GPUPhysicsChainDispatcher.GPUParticleData> data, int generation, long submissionId)
        {
            _readback.Clear();
            if (_readback.Capacity < data.Length)
                _readback.Capacity = data.Length;
            for (int i = 0; i < data.Length; ++i)
                _readback.Add(new(data[i].Position, data[i].PrevPosition, data[i].IsColliding, data[i].PreviousPhysicsPosition));
            _chain.ApplyGpuReadback(CollectionsMarshal.AsSpan(_readback), generation, submissionId);
        }
        public void AppendBatchedGpuDrivenBonePaletteBindings(int particleBaseOffset, List<GPUPhysicsChainDispatcher.GpuDrivenRendererPaletteBinding> bindings)
        {
            for (int i = 0; i < _paletteStates.Count; ++i)
            {
                PaletteState state = _paletteStates[i];
                if (state.BoneBufferGeneration != state.Renderer.BoneBufferGeneration)
                {
                    _paletteBindingsDirty = true;
                    continue;
                }
                bindings.Add(new(this, state.Renderer, state.Mappings, particleBaseOffset,
                    state.BoneMatrixElementCount, state.Complete, _bindingGeneration,
                    _particleStateVersion, state.BoneBufferGeneration));
            }
        }
        public void ClearBatchedGpuDrivenBonePaletteSources()
        { for (int i = 0; i < _paletteStates.Count; ++i) _paletteStates[i].Renderer.ClearGpuDrivenSkinPaletteSource(this); }
        public bool PublishGpuDrivenBoneMatrices(XRDataBuffer? particlesBuffer, XRDataBuffer? transformsBuffer, int particleBaseOffset, bool includeCompletePalettes = true, IPhysicsChainComputeBackend? backend = null) => true;

        public void AcceptGpuDrivenBoneBindings(ReadOnlySpan<PhysicsChainGpuBone> bones,
            int boneStructureSignature, int particleStateVersion, bool effectiveGpuDrivenSkinning)
        {
            bool modeChanged = effectiveGpuDrivenSkinning != _effectiveGpuMode;
            if (modeChanged)
            {
                _effectiveGpuMode = effectiveGpuDrivenSkinning;
                ReleasePaletteBindings();
            }
            _particleStateVersion = particleStateVersion;
            if (modeChanged || _copiedBoneStructureSignature != boneStructureSignature || _bones.Count != bones.Length)
            {
                CopyBones(bones);
                _copiedBoneStructureSignature = boneStructureSignature;
            }
            _ = RefreshGpuDrivenBoneBindings();
        }

        private void CopyBones(ReadOnlySpan<PhysicsChainGpuBone> values)
        {
            _bones.Clear();
            for (int i = 0; i < values.Length; ++i) _bones.Add(values[i]);
            RebuildPaletteBindings();
        }

        private void RebuildPaletteBindings()
        {
            _paletteBindingsDirty = false;
            _paletteBindingRetryPending = false;
            ReleasePaletteBindings();
            unchecked { ++_bindingGeneration; }
            _particleIndices.Clear(); _firstChildren.Clear();
            if (!_effectiveGpuMode || _chain.SceneNode is null)
                return;
            for (int i = 0; i < _bones.Count; ++i)
            {
                PhysicsChainGpuBone bone = _bones[i];
                if (bone.Transform is not null) _particleIndices[bone.Transform] = i;
                if (bone.ParentIndex >= 0 && !_firstChildren.ContainsKey(bone.ParentIndex)) _firstChildren[bone.ParentIndex] = i;
            }
            var root = _chain.SceneNode.Parent ?? _chain.SceneNode;
            root.IterateComponents<ModelComponent>(model =>
            {
                foreach (XRMeshRenderer renderer in model.GetAllRenderersWhere(static value => value.Mesh?.HasSkinning == true))
                    TryAddPaletteBinding(renderer);
            }, true);
            if (!_paletteBindingRetryPending)
            {
                _paletteRetryRenderer = null;
                _paletteRetryAttempted = false;
            }
        }

        public void InvalidateGpuDrivenRenderers()
        {
            Interlocked.Increment(ref _bindingInvalidationRevision);
        }

        public bool RefreshGpuDrivenBoneBindings()
        {
            int revision = Volatile.Read(ref _bindingInvalidationRevision);
            if (revision != _bindingAppliedRevision)
            {
                RebuildPaletteBindings();
                _bindingAppliedRevision = revision;
                return true;
            }

            if (!_paletteBindingsDirty && !_paletteBindingRetryPending)
                return false;

            _paletteBindingsDirty = false;
            if (_paletteBindingRetryPending && _paletteRetryRenderer is { } retryRenderer &&
                (!_paletteRetryAttempted || retryRenderer.BoneBufferGeneration != _paletteRetryGeneration))
            {
                _paletteRetryGeneration = retryRenderer.BoneBufferGeneration;
                _paletteRetryAttempted = true;
                RebuildPaletteBindings();
                return true;
            }

            for (int i = 0; i < _paletteStates.Count; ++i)
            {
                PaletteState state = _paletteStates[i];
                if (state.BoneBufferGeneration == state.Renderer.BoneBufferGeneration)
                    continue;
                RebuildPaletteBindings();
                return true;
            }

            return false;
        }

        public void ReleasePaletteBindings()
        {
            for (int i = 0; i < _paletteStates.Count; ++i)
            {
                PaletteState state = _paletteStates[i];
                state.Renderer.GpuDrivenBoneCoverageChanged -= RendererCoverageChanged;
                state.Renderer.ClearGpuDrivenSkinPaletteSource(this);
                state.Renderer.UnregisterGpuDrivenBoneIndices(state.Indices, state.BoneBufferGeneration);
            }
            _paletteStates.Clear();
        }

        private void TryAddPaletteBinding(XRMeshRenderer renderer)
        {
            if (!renderer.EnsureSkinningBuffers())
            {
                QueuePaletteBindingRetry(renderer);
                return;
            }
            (XRMesh? mesh, long expectedGeneration) = renderer.CaptureGpuDrivenBoneMappingSource();
            if (mesh?.UtilizedBones is not { Length: > 0 })
            {
                QueuePaletteBindingRetry(renderer);
                return;
            }
            var mappings = new List<GPUPhysicsChainDispatcher.GPUDrivenBoneMappingData>();
            var indices = new List<uint>();
            for (int boneIndex = 0; boneIndex < mesh.UtilizedBones.Length; ++boneIndex)
            {
                var (transform, _) = mesh.UtilizedBones[boneIndex];
                if (transform is not Transform bone || !_particleIndices.TryGetValue(bone, out int particleIndex)) continue;
                int child = _firstChildren.GetValueOrDefault(particleIndex, -1);
                mappings.Add(new() { ParticleIndex = particleIndex, ChildParticleIndex = child, BoneMatrixIndex = boneIndex + 1, Flags = child >= 0 ? 1 : 0, RestLocalDirection = child >= 0 ? _bones[child].RestLocalDirection : Vector3.Zero });
                indices.Add((uint)(boneIndex + 1));
            }
            if (mappings.Count == 0) return;
            uint[] driven = indices.ToArray();
            long boneBufferGeneration = renderer.RegisterGpuDrivenBoneIndices(driven, expectedGeneration, mesh);
            if (boneBufferGeneration == 0)
            {
                QueuePaletteBindingRetry(renderer);
                return;
            }
            _paletteStates.Add(new(renderer, mappings.ToArray(), driven,
                (uint)mesh.UtilizedBones.Length + 1u,
                mappings.Count == mesh.UtilizedBones.Length, boneBufferGeneration));
            renderer.GpuDrivenBoneCoverageChanged += RendererCoverageChanged;
        }

        private void QueuePaletteBindingRetry(XRMeshRenderer renderer)
        {
            if (!ReferenceEquals(_paletteRetryRenderer, renderer))
            {
                _paletteRetryRenderer = renderer;
                _paletteRetryGeneration = renderer.BoneBufferGeneration;
                _paletteRetryAttempted = false;
            }
            _paletteBindingRetryPending = true;
        }

        private void RendererCoverageChanged(XRMeshRenderer renderer, GpuDrivenBoneCoverageSnapshot snapshot)
            => _paletteBindingsDirty = true;

        private sealed record PaletteState(
            XRMeshRenderer Renderer,
            GPUPhysicsChainDispatcher.GPUDrivenBoneMappingData[] Mappings,
            uint[] Indices,
            uint BoneMatrixElementCount,
            bool Complete,
            long BoneBufferGeneration);
    }

    private sealed class PhysicsChainReadbackCoordinatorAdapter(PhysicsChainWorld world) : IPhysicsChainReadbackCoordinator
    {
        private int _activeSourceCount;
        public PhysicsChainWorld World { get; } = world;
        public bool HasActiveSources => Volatile.Read(ref _activeSourceCount) > 0;
        public bool HasPendingTransfers => World.HasPendingReadbackTransfers();

        public void AddSource() => Interlocked.Increment(ref _activeSourceCount);
        public void RemoveSource() => Interlocked.Decrement(ref _activeSourceCount);

        public PhysicsChainReadbackTransferCounters GetReadbackTransferCounters()
            => World.GetReadbackTransferCounters();
        public int BuildPendingReadbackGatherPlans(PhysicsChainReadbackSourceEpoch sourceEpoch, long gatherFrame, Span<PhysicsChainReadbackGatherPlan?> destination)
            => World.BuildPendingReadbackGatherPlans(sourceEpoch, gatherFrame, destination);
        public bool TryAcquireReadbackStagingSlot(PhysicsChainReadbackGatherPlan plan, out PhysicsChainReadbackStagingLease lease, out PhysicsChainReadbackTransferFailure failure)
            => World.TryAcquireReadbackStagingSlot(plan, out lease, out failure);
        public bool FailReadbackStagingSlot(PhysicsChainReadbackStagingLease lease, long completionFrame)
            => World.FailReadbackStagingSlot(lease, completionFrame);
        public bool CommitReadbackStagingSlot(PhysicsChainReadbackStagingLease lease, IPhysicsChainReadbackStagingSource source, IPhysicsChainReadbackFence fence, long transferFrame, out PhysicsChainReadbackTransferFailure failure)
            => World.CommitReadbackStagingSlot(lease, source, fence, transferFrame, out failure);
        public void PollReadbackTransfers(long currentFrame, PhysicsChainReadbackSourceEpoch currentEpoch)
            => World.PollReadbackTransfers(currentFrame, currentEpoch);
    }
}
