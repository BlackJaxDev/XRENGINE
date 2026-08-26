using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Transforms;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Rendering-side installation point for physics-chain graphics services.  The
/// Core assembly deliberately knows only <see cref="IRuntimePhysicsChainRenderingBridge"/>.
/// </summary>
public sealed class RuntimePhysicsChainRenderingBridge : IRuntimePhysicsChainRenderingBridge
{
    private readonly Dictionary<PhysicsChainComponent, Source> _sources = [];
    public static RuntimePhysicsChainRenderingBridge Instance { get; } = new();

    private RuntimePhysicsChainRenderingBridge() { }

    public PhysicsChainGpuBackendState BackendState
        => (PhysicsChainGpuBackendState)GPUPhysicsChainDispatcher.Instance.BackendStatus.State;

    /// <summary>Installs the renderer implementation after the rendering runtime has initialized.</summary>
    public static void Install()
        => RuntimePhysicsChainRendering.Install(Instance);

    public void Register(PhysicsChainComponent chain)
    {
        if (_sources.TryGetValue(chain, out Source? existing))
        {
            if (!GPUPhysicsChainDispatcher.Instance.IsRegistered(existing))
                GPUPhysicsChainDispatcher.Instance.Register(existing);
            return;
        }

        Source source = new(chain);
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
    }

    public void Execute(PhysicsChainComponent chain, in PhysicsChainGpuDispatchSnapshot snapshot)
    {
        if (!_sources.TryGetValue(chain, out Source? source))
            return;

        source.Copy(snapshot);
        GPUPhysicsChainDispatcher.Instance.SubmitData(source, source.Particles, source.ParticleStatic, source.Trees,
            source.Transforms, source.Colliders, snapshot.DeltaTime, snapshot.ObjectScale, snapshot.Weight,
            snapshot.Force, snapshot.Gravity, snapshot.ObjectMove, snapshot.FreezeAxis, snapshot.LoopCount,
            snapshot.TimeVar, snapshot.ExecutionGeneration, snapshot.SubmissionId, snapshot.StaticDataVersion,
            snapshot.ParticleStateVersion, snapshot.TransformSignature, snapshot.ColliderSignature);
    }

    public void RenderDebug(PhysicsChainComponent chain)
        => GPUPhysicsChainDispatcher.Instance.RenderSelectedGpuDebug();

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
        public readonly List<GPUPhysicsChainDispatcher.GPUParticleData> Particles = [];
        public readonly List<GPUPhysicsChainDispatcher.GPUParticleStaticData> ParticleStatic = [];
        public readonly List<GPUPhysicsChainDispatcher.GPUParticleTreeData> Trees = [];
        public readonly List<Matrix4x4> Transforms = [];
        public readonly List<GPUPhysicsChainDispatcher.GPUColliderData> Colliders = [];
        private readonly List<PhysicsChainGpuParticle> _readback = [];
        private readonly List<PhysicsChainGpuBone> _bones = [];
        private readonly Dictionary<Transform, int> _particleIndices = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, int> _firstChildren = [];
        private readonly List<PaletteState> _paletteStates = [];
        private int _boneSignature = int.MinValue;
        private PhysicsChainReadbackCoordinatorAdapter? _readbackCoordinator;

        public Guid ID => _chain.ID;
        public IRuntimeWorldContext? World => _chain.World;
        public PhysicsChainRuntimeHandle RuntimeHandle => _chain.RuntimeHandle;
        public IPhysicsChainReadbackCoordinator? ReadbackCoordinator
        {
            get
            {
                if (_chain.World is null || !PhysicsChainWorld.TryGet(_chain.World, out PhysicsChainWorld? world) || world is null)
                    return null;
                if (_readbackCoordinator?.World != world)
                    _readbackCoordinator = new PhysicsChainReadbackCoordinatorAdapter(world);
                return _readbackCoordinator;
            }
        }
        public int UpdateMode => (int)_chain.UpdateMode;
        public bool UseBatchedDispatcher => _chain.UseBatchedDispatcher;
        public bool HasGpuDrivenRenderers => _paletteStates.Count > 0;
        public bool DebugDrawChains => _chain.DebugDrawChains;
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
            { PaletteState state = _paletteStates[i]; bindings.Add(new(this, state.Renderer, state.Mappings, particleBaseOffset, state.BoneMatrixElementCount, state.Complete, _boneSignature, 0)); }
        }
        public void ClearBatchedGpuDrivenBonePaletteSources()
        { for (int i = 0; i < _paletteStates.Count; ++i) _paletteStates[i].Renderer.ClearGpuDrivenSkinPaletteSource(this); }
        public bool PublishGpuDrivenBoneMatrices(XRDataBuffer? particlesBuffer, XRDataBuffer? transformsBuffer, int particleBaseOffset, bool includeCompletePalettes = true, IPhysicsChainComputeBackend? backend = null) => true;

        public void Copy(in PhysicsChainGpuDispatchSnapshot snapshot)
        {
            CopyParticles(snapshot.Particles);
            CopyStatic(snapshot.ParticleStatic);
            CopyTrees(snapshot.Trees);
            CopyMatrices(snapshot.Transforms);
            CopyColliders(snapshot.Colliders);
            CopyBones(snapshot.Bones);
        }

        private void CopyParticles(ReadOnlySpan<PhysicsChainGpuParticle> values)
        {
            Particles.Clear();
            for (int i = 0; i < values.Length; ++i)
                Particles.Add(new() { Position = values[i].Position, PrevPosition = values[i].PrevPosition, IsColliding = values[i].IsColliding, PreviousPhysicsPosition = values[i].PreviousPhysicsPosition });
        }
        private void CopyStatic(ReadOnlySpan<PhysicsChainGpuParticleStatic> values)
        {
            ParticleStatic.Clear();
            for (int i = 0; i < values.Length; ++i)
            { var value = values[i]; ParticleStatic.Add(new() { TransformLocalPosition = value.TransformLocalPosition, ParentIndex = value.ParentIndex, Damping = value.Damping, Elasticity = value.Elasticity, Stiffness = value.Stiffness, Inert = value.Inert, Friction = value.Friction, Radius = value.Radius, BoneLength = value.BoneLength, TreeIndex = value.TreeIndex }); }
        }
        private void CopyTrees(ReadOnlySpan<PhysicsChainGpuTree> values)
        { Trees.Clear(); for (int i = 0; i < values.Length; ++i) Trees.Add(new() { RestGravity = values[i].RestGravity, ParticleOffset = values[i].ParticleOffset, ParticleCount = values[i].ParticleCount }); }
        private void CopyMatrices(ReadOnlySpan<Matrix4x4> values)
        { Transforms.Clear(); for (int i = 0; i < values.Length; ++i) Transforms.Add(values[i]); }
        private void CopyColliders(ReadOnlySpan<PhysicsChainGpuCollider> values)
        { Colliders.Clear(); for (int i = 0; i < values.Length; ++i) Colliders.Add(new() { Center = values[i].Center, Params = values[i].Params, Orientation = values[i].Orientation, Type = values[i].Type }); }

        private void CopyBones(ReadOnlySpan<PhysicsChainGpuBone> values)
        {
            var hash = new HashCode();
            for (int i = 0; i < values.Length; ++i) { hash.Add(values[i].Transform); hash.Add(values[i].ParentIndex); }
            int signature = hash.ToHashCode();
            if (signature == _boneSignature)
                return;
            _boneSignature = signature;
            _bones.Clear();
            for (int i = 0; i < values.Length; ++i) _bones.Add(values[i]);
            RebuildPaletteBindings();
        }

        private void RebuildPaletteBindings()
        {
            ClearBatchedGpuDrivenBonePaletteSources();
            _paletteStates.Clear(); _particleIndices.Clear(); _firstChildren.Clear();
            if (!_chain.UseGpuDrivenSkinning || _chain.SceneNode is null)
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
        }

        public void InvalidateGpuDrivenRenderers()
        {
            _boneSignature = int.MinValue;
            RebuildPaletteBindings();
        }

        private void TryAddPaletteBinding(XRMeshRenderer renderer)
        {
            XRMesh? mesh = renderer.Mesh;
            if (mesh?.UtilizedBones is not { Length: > 0 } || !renderer.EnsureSkinningBuffers())
                return;
            var mappings = new List<GPUPhysicsChainDispatcher.GPUDrivenBoneMappingData>();
            var indices = new List<uint>();
            for (int boneIndex = 0; boneIndex < mesh.UtilizedBones.Length; ++boneIndex)
            {
                var (transform, _) = mesh.UtilizedBones[boneIndex];
                if (transform is not Transform bone || !_particleIndices.TryGetValue(bone, out int particleIndex)) continue;
                int child = _firstChildren.GetValueOrDefault(particleIndex, -1);
                mappings.Add(new() { ParticleIndex = particleIndex, ChildParticleIndex = child, BoneMatrixIndex = boneIndex + 1, Flags = child >= 0 ? 1 : 0, RestLocalDirection = child >= 0 ? _bones[particleIndex].RestLocalDirection : Vector3.Zero });
                indices.Add((uint)(boneIndex + 1));
            }
            if (mappings.Count == 0) return;
            uint[] driven = indices.ToArray(); renderer.RegisterGpuDrivenBoneIndices(driven);
            _paletteStates.Add(new(renderer, mappings.ToArray(), driven, (uint)mesh.UtilizedBones.Length + 1u, mappings.Count == mesh.UtilizedBones.Length));
        }

        private sealed record PaletteState(XRMeshRenderer Renderer, GPUPhysicsChainDispatcher.GPUDrivenBoneMappingData[] Mappings, uint[] Indices, uint BoneMatrixElementCount, bool Complete);
    }

    private sealed class PhysicsChainReadbackCoordinatorAdapter(PhysicsChainWorld world) : IPhysicsChainReadbackCoordinator
    {
        public PhysicsChainWorld World { get; } = world;

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
