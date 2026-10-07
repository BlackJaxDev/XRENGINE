using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private bool _pendingGpuExecutionReconfigure;
    private bool _gpuBridgeRegistered;
    private readonly List<PhysicsChainGpuParticle> _gpuParticles = [];
    private readonly List<PhysicsChainGpuParticleStatic> _gpuParticleStatic = [];
    private readonly List<PhysicsChainGpuTree> _gpuTrees = [];
    private readonly List<Matrix4x4> _gpuTransforms = [];
    private readonly List<PhysicsChainGpuCollider> _gpuColliders = [];
    private readonly List<PhysicsChainGpuBone> _gpuBones = [];
    private bool _gpuInputPrepared;
    private int _gpuParticleSourceVersion;
    private int _gpuStaticSourceVersion;
    private int _gpuStaticDataVersion;
    private int _gpuTreeDataVersion;
    private int _gpuBoneSignature;
    private int _gpuTransformSignature;
    private int _gpuColliderSignature;
    private int _gpuExecutionGeneration;
    private long _gpuSubmissionId;
    private long _lastAppliedGpuSubmissionId;

    /// <summary>Renderer supplied GPU backend state; unavailable is explicit and never selects a CPU fallback.</summary>
    public PhysicsChainGpuBackendState GpuBackendState => RuntimePhysicsChainRendering.Current.BackendState;

    /// <summary>Reads cumulative timing for this component's physics chain world.</summary>
    public PhysicsChainLateTickTelemetrySnapshot? WorldLateTickTelemetry
        => World is { } world && PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler)
            ? scheduler?.LateTickTelemetry
            : null;

    private void ActivateGpuExecutionMode()
    {
        if (!UseGPU || !IsActiveInHierarchy || _gpuBridgeRegistered)
            return;

        RuntimePhysicsChainRendering.Current.Register(this);
        _gpuBridgeRegistered = true;
    }

    private void DeactivateGpuExecutionMode()
    {
        if (_gpuBridgeRegistered)
            RuntimePhysicsChainRendering.Current.Unregister(this);

        _gpuBridgeRegistered = false;
        unchecked { ++_gpuExecutionGeneration; }
    }

    private bool HandleGpuExecutionModePropertyChanged<T>(string? propertyName, T previous, T current)
    {
        if (propertyName is not (nameof(UseGPU) or nameof(UseBatchedDispatcher)))
            return false;

        if (_isSimulating)
        {
            _pendingGpuExecutionReconfigure = true;
            return true;
        }

        ReconfigureGpuExecutionMode();
        return true;
    }

    private void ApplyPendingGpuExecutionReconfigure()
    {
        if (!_pendingGpuExecutionReconfigure)
            return;

        _pendingGpuExecutionReconfigure = false;
        ReconfigureGpuExecutionMode();
    }

    private void ReconfigureGpuExecutionMode()
    {
        DeactivateGpuExecutionMode();
        if (DefaultTransform is null)
            return;

        if (IsActiveInHierarchy)
            ActivateGpuExecutionMode();
    }

    private void MarkGpuBuffersDirty() { }

    private void ExecuteGpuLateUpdate()
    {
        bool observe = RuntimeWorldTickTelemetry.Enabled;
        long prepareStart = observe ? Stopwatch.GetTimestamp() : 0L;
        CheckDistance();
        if (!IsNeedUpdate())
        {
            if (observe)
                PhysicsChainWorld.RecordGpuComponentPreparation(Stopwatch.GetTimestamp() - prepareStart);
            return;
        }

        Prepare(snapshotForCpuJobs: false);
        if (_particleTrees.Count == 0)
        {
            _lastSimulationProducedResults = false;
            if (observe)
                PhysicsChainWorld.RecordGpuComponentPreparation(Stopwatch.GetTimestamp() - prepareStart);
            return;
        }

        ResolveSimulationLoopAndTimeScale(_deltaTime, out int loopCount, out float timeVar);
        _lastSimulationProducedResults = loopCount > 0;
        if (observe)
            PhysicsChainWorld.RecordGpuComponentPreparation(Stopwatch.GetTimestamp() - prepareStart);
        if (loopCount == 0)
            return;

        long packingStart = observe ? Stopwatch.GetTimestamp() : 0L;
        PrepareGpuDispatchData();
        if (observe)
            PhysicsChainWorld.RecordGpuInputPacking(Stopwatch.GetTimestamp() - packingStart);
        long dispatchStart = observe ? Stopwatch.GetTimestamp() : 0L;
        RuntimePhysicsChainRendering.Current.Execute(this, new PhysicsChainGpuDispatchSnapshot(
            CollectionsMarshal.AsSpan(_gpuParticles), CollectionsMarshal.AsSpan(_gpuParticleStatic),
            CollectionsMarshal.AsSpan(_gpuTrees), CollectionsMarshal.AsSpan(_gpuTransforms), CollectionsMarshal.AsSpan(_gpuColliders), CollectionsMarshal.AsSpan(_gpuBones),
            _deltaTime, _objectScale, _weight, Force, Gravity, _objectMove, (int)FreezeAxis,
            loopCount, timeVar, _gpuExecutionGeneration, ++_gpuSubmissionId,
            _gpuStaticDataVersion, _gpuTreeDataVersion, _particleStateVersion,
            _gpuTransformSignature, _gpuColliderSignature,
            _gpuBoneSignature));
        if (observe)
            PhysicsChainWorld.RecordGpuBridgeDispatch(Stopwatch.GetTimestamp() - dispatchStart);
    }

    private void ApplyPendingGpuBoneSync()
    {
        if (!UseGPU || !_hasPendingGpuBoneSync)
            return;

        _hasPendingGpuBoneSync = false;
        ApplyCurrentParticleTransforms(newSimulationResults: true);
    }

    internal bool RequiresGpuReadback() => GpuSyncToBones;

    internal void NotifyGpuReadbackUnavailable(string reason)
    {
        if (!GpuSyncToBones)
            return;

        LogFault($"GpuReadbackUnavailable:{GetHashCode()}:{reason}",
            $"Async GPU readback was unavailable for compatibility sync mode on {FormatRoot(Root)}. Keeping the previous CPU bone pose. Reason={reason}.");
        RuntimePhysicsChainRendering.Current.NotifyReadbackUnavailable(this, reason);
    }

    internal void ApplyGpuReadback(ReadOnlySpan<PhysicsChainGpuParticle> readback, int generation, long submissionId)
    {
        if (!UseGPU || generation != _gpuExecutionGeneration || submissionId <= _lastAppliedGpuSubmissionId)
            return;

        int index = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
            for (int particleIndex = 0; particleIndex < _particleTrees[treeIndex].Particles.Count && index < readback.Length; ++particleIndex, ++index)
            {
                Particle particle = _particleTrees[treeIndex].Particles[particleIndex];
                PhysicsChainGpuParticle value = readback[index];
                particle.Position = value.Position;
                particle.PrevPosition = value.PrevPosition;
                particle.PreviousPhysicsPosition = value.PreviousPhysicsPosition;
                particle.IsColliding = value.IsColliding != 0;
            }

        _lastAppliedGpuSubmissionId = submissionId;
        if (GpuSyncToBones)
            _hasPendingGpuBoneSync = true;
    }

    /// <summary>Requests the rendering adapter to rebuild optional GPU skinning bindings.</summary>
    public void InvalidateGpuDrivenRenderers()
        => RuntimePhysicsChainRendering.Current.InvalidateGpuDrivenRenderers(this);

    private void RenderGpuDebug()
        => RuntimePhysicsChainRendering.Current.RenderDebug(this);

    private void PrepareGpuDispatchData()
    {
        HashCode transformHash = new();
        HashCode colliderHash = new();
        HashCode boneHash = new();
        bool rebuildStatic = !_gpuInputPrepared || _gpuStaticSourceVersion != _particlesVersion;
        bool rebuildParticles = rebuildStatic || _gpuParticleSourceVersion != _particleStateVersion;
        bool treeChanged = rebuildStatic;
        if (rebuildParticles)
            _gpuParticles.Clear();
        if (rebuildStatic)
        {
            _gpuParticleStatic.Clear();
            _gpuTrees.Clear();
            _gpuBones.Clear();
        }
        _gpuTransforms.Clear();
        _gpuColliders.Clear();
        int particleOffset = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            if (rebuildStatic)
                _gpuTrees.Add(new(tree.RestGravity, particleOffset, tree.Particles.Count));
            else if (_gpuTrees[treeIndex].RestGravity != tree.RestGravity)
            {
                _gpuTrees[treeIndex] = _gpuTrees[treeIndex] with { RestGravity = tree.RestGravity };
                treeChanged = true;
            }
            for (int particleIndex = 0; particleIndex < tree.Particles.Count; ++particleIndex)
            {
                Particle particle = tree.Particles[particleIndex];
                if (rebuildParticles)
                    _gpuParticles.Add(new(particle.Position, particle.PrevPosition, particle.IsColliding ? 1 : 0, particle.PreviousPhysicsPosition));
                if (rebuildStatic)
                {
                    _gpuParticleStatic.Add(new(particle.TransformLocalPosition,
                        particle.ParentIndex >= 0 ? particle.ParentIndex + particleOffset : -1,
                        particle.Damping, particle.Elasticity, particle.Stiffness, particle.Inert, particle.Friction,
                        particle.Radius, particle.SegmentLength, treeIndex));
                    PhysicsChainGpuBone bone = new(particle.Transform,
                        particle.ParentIndex >= 0 ? particle.ParentIndex + particleOffset : -1,
                        particle.Transform is not null ? particle.InitLocalPosition : particle.EndOffset);
                    _gpuBones.Add(bone);
                    boneHash.Add(bone.Transform);
                    boneHash.Add(bone.ParentIndex);
                    boneHash.Add(bone.RestLocalDirection);
                }
                // Root motion uses its transform matrix. Only child local offsets
                // are read from the static template by the GPU solver.
                Matrix4x4 transformMatrix = particle.Transform is not null
                    ? particle.TransformLocalToWorldMatrix
                    : particle.ParentIndex >= 0
                        ? tree.Particles[particle.ParentIndex].TransformLocalToWorldMatrix
                        : Matrix4x4.Identity;
                _gpuTransforms.Add(transformMatrix);
                transformHash.Add(transformMatrix);
            }
            particleOffset += tree.Particles.Count;
        }

        if (rebuildStatic)
            unchecked { ++_gpuStaticDataVersion; }
        if (treeChanged)
            unchecked { ++_gpuTreeDataVersion; }
        if (rebuildStatic)
            _gpuBoneSignature = boneHash.ToHashCode();
        _gpuStaticSourceVersion = _particlesVersion;
        _gpuParticleSourceVersion = _particleStateVersion;
        _gpuInputPrepared = true;

        if (_effectiveColliders is not null)
            for (int i = 0; i < _effectiveColliders.Count; ++i)
                AppendGpuCollider(_effectiveColliders[i]);

        for (int i = 0; i < _gpuColliders.Count; ++i)
        {
            PhysicsChainGpuCollider collider = _gpuColliders[i];
            colliderHash.Add(collider.Center);
            colliderHash.Add(collider.Params);
            colliderHash.Add(collider.Orientation);
            colliderHash.Add(collider.Type);
        }

        _gpuTransformSignature = transformHash.ToHashCode();
        _gpuColliderSignature = colliderHash.ToHashCode();
    }

    private void AppendGpuCollider(PhysicsChainColliderBase collider)
    {
        if (collider is PhysicsChainSphereCollider sphere)
        {
            TransformBase transform = sphere.ColliderTransform ?? sphere.Transform;
            _gpuColliders.Add(new(new Vector4(transform.WorldTranslation, sphere.Radius), default, default, 0));
        }
        else if (collider is PhysicsChainCapsuleCollider capsule)
        {
            TransformBase transform = capsule.ColliderTransform ?? capsule.Transform;
            Vector3 halfAxis = transform.WorldUp * (capsule.Height * 0.5f);
            Vector3 start = transform.WorldTranslation - halfAxis;
            Vector3 end = transform.WorldTranslation + halfAxis;
            float lengthSquared = Vector3.DistanceSquared(start, end);
            _gpuColliders.Add(new(new Vector4(start, capsule.Radius), new Vector4(end, lengthSquared > 1e-8f ? 1.0f / lengthSquared : 0.0f), default, 1));
        }
        else if (collider is PhysicsChainBoxCollider box)
        {
            TransformBase transform = box.ColliderTransform ?? box.Transform;
            Quaternion rotation = transform.WorldRotation;
            _gpuColliders.Add(new(new Vector4(transform.WorldTranslation, 0.0f), new Vector4(Vector3.Abs(box.Size) * Vector3.Abs(transform.LossyWorldScale) * 0.5f, 0.0f), new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W), 2));
        }
        else if (collider is PhysicsChainPlaneCollider plane)
            _gpuColliders.Add(new(new Vector4(plane.Transform.TransformPoint(plane._center), 0.0f), new Vector4(plane._plane.Normal, plane._bound == PhysicsChainColliderBase.EBound.Inside ? 1.0f : 0.0f), default, 3));
    }
}
