using System.Numerics;
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
    private int _gpuStaticVersion = -1;
    private int _gpuParticleVersion = -1;
    private int _gpuExecutionGeneration;
    private long _gpuSubmissionId;
    private long _lastAppliedGpuSubmissionId;

    /// <summary>Renderer supplied GPU backend state; unavailable is explicit and never selects a CPU fallback.</summary>
    public PhysicsChainGpuBackendState GpuBackendState => RuntimePhysicsChainRendering.Current.BackendState;

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
        CheckDistance();
        if (!IsNeedUpdate())
            return;

        Prepare();
        PrepareGpuDispatchData();
        RuntimePhysicsChainRendering.Current.Execute(this, new PhysicsChainGpuDispatchSnapshot(
            CollectionsMarshal.AsSpan(_gpuParticles), CollectionsMarshal.AsSpan(_gpuParticleStatic),
            CollectionsMarshal.AsSpan(_gpuTrees), CollectionsMarshal.AsSpan(_gpuTransforms), CollectionsMarshal.AsSpan(_gpuColliders), CollectionsMarshal.AsSpan(_gpuBones),
            _deltaTime, _objectScale, _weight, Force, Gravity, _objectMove, (int)FreezeAxis,
            1, 1.0f, _gpuExecutionGeneration, ++_gpuSubmissionId, _gpuStaticVersion, _gpuParticleVersion, 0, 0));
        _lastSimulationProducedResults = true;
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
        _gpuParticles.Clear();
        _gpuParticleStatic.Clear();
        _gpuTrees.Clear();
        _gpuTransforms.Clear();
        _gpuColliders.Clear();
        _gpuBones.Clear();
        int particleOffset = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            _gpuTrees.Add(new(tree.RestGravity, particleOffset, tree.Particles.Count));
            for (int particleIndex = 0; particleIndex < tree.Particles.Count; ++particleIndex)
            {
                Particle particle = tree.Particles[particleIndex];
                _gpuParticles.Add(new(particle.Position, particle.PrevPosition, particle.IsColliding ? 1 : 0, particle.PreviousPhysicsPosition));
                _gpuParticleStatic.Add(new(particle.TransformLocalPosition,
                    particle.ParentIndex >= 0 ? particle.ParentIndex + particleOffset : -1,
                    particle.Damping, particle.Elasticity, particle.Stiffness, particle.Inert, particle.Friction,
                    particle.Radius, particle.SegmentLength, treeIndex));
                _gpuTransforms.Add(particle.Transform is not null ? particle.TransformLocalToWorldMatrix : Matrix4x4.Identity);
                _gpuBones.Add(new(particle.Transform, particle.ParentIndex >= 0 ? particle.ParentIndex + particleOffset : -1, particle.Transform is not null ? particle.InitLocalPosition : particle.EndOffset));
            }
            particleOffset += tree.Particles.Count;
        }

        if (_effectiveColliders is not null)
            for (int i = 0; i < _effectiveColliders.Count; ++i)
                AppendGpuCollider(_effectiveColliders[i]);

        unchecked
        {
            ++_gpuStaticVersion;
            ++_gpuParticleVersion;
        }
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
