using System.Numerics;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private PhysicsChainWorld? _cpuBackendWorld;
    private PhysicsChainCpuBackend? _cpuBackend;
    private PhysicsChainArenaHandle _cpuBackendHandle = PhysicsChainArenaHandle.Invalid;
    private PhysicsChainCpuTreeInput[] _cpuTreeInputs = [];
    private PhysicsChainCpuParticleInput[] _cpuParticleInputs = [];
    private PhysicsChainCpuState[] _cpuInitialStates = [];
    private PhysicsChainCpuOutput[] _cpuPublishedOutputs = [];
    private PhysicsChainCpuCollider[] _cpuColliders = [];
    private PhysicsChainCpuSharedColliderSet? _cpuSharedColliderSet;
    private int _cpuBackendParticleVersion = -1;
    private int _cpuBackendStateVersion = -1;
    private int _cpuStepCount;
    private float _cpuStepTime;
    private bool _usePreparedCpuBackend;
    private bool _cpuBatchSolved;
    private bool _cpuTransformMirrorEnabled = true;
    private int _cpuTransformMirrorIntervalFrames = 1;
    private bool _cpuRegisteredMirrorEnabled;
    private int _cpuRegisteredMirrorIntervalFrames;
    private bool _cpuMirrorPublishedThisFrame;

    public bool CpuTransformMirrorEnabled
    {
        get => _cpuTransformMirrorEnabled;
        set
        {
            if (SetField(ref _cpuTransformMirrorEnabled, value))
                _cpuBackendParticleVersion = -1;
        }
    }

    public int CpuTransformMirrorIntervalFrames
    {
        get => _cpuTransformMirrorIntervalFrames;
        set
        {
            int normalized = Math.Max(value, 1);
            if (SetField(ref _cpuTransformMirrorIntervalFrames, normalized))
                _cpuBackendParticleVersion = -1;
        }
    }

    /// <summary>
    /// Copies the latest direct CPU palette when the component is routed
    /// through the world-owned data-oriented backend.
    /// </summary>
    public bool TryCopyCpuCurrentPalette(Span<Matrix4x4> destination)
        => _cpuBackend is not null
            && _cpuBackend.TryCopyCurrentPalette(_cpuBackendHandle, destination);

    /// <summary>Copies the previous direct CPU palette used by motion consumers.</summary>
    public bool TryCopyCpuPreviousPalette(Span<Matrix4x4> destination)
        => _cpuBackend is not null
            && _cpuBackend.TryCopyPreviousPalette(_cpuBackendHandle, destination);

    /// <summary>Returns conservative bounds generated without hierarchy mutation.</summary>
    public bool TryGetCpuBounds(out PhysicsChainCpuBounds bounds)
    {
        if (_cpuBackend is not null && _cpuBackend.TryGetBounds(_cpuBackendHandle, out bounds))
            return true;
        bounds = PhysicsChainCpuBounds.Invalid;
        return false;
    }

    private void HoldCpuOutputHistory()
        => _cpuBackend?.HoldOutputHistory(_cpuBackendHandle);

    /// <summary>Returns stable direct-render slices shared by every CPU renderer consumer.</summary>
    public bool TryGetCpuRenderOutput(out PhysicsChainCpuRenderOutput output)
    {
        if (_cpuBackend is not null)
            return _cpuBackend.TryGetRenderOutput(_cpuBackendHandle, out output);
        output = default;
        return false;
    }

    internal void AttachCpuBackend(PhysicsChainWorld world, PhysicsChainCpuBackend backend)
    {
        _cpuBackendWorld = world;
        _cpuBackend = backend;
    }

    internal void DetachCpuBackend()
    {
        if (_cpuBackend is not null && _cpuBackendHandle.IsValid)
            _cpuBackend.Remove(_cpuBackendHandle);
        _cpuBackendHandle = PhysicsChainArenaHandle.Invalid;
        _cpuBackend = null;
        _cpuBackendWorld = null;
        _cpuSharedColliderSet = null;
        _usePreparedCpuBackend = false;
    }


    internal bool TryGetPreparedCpuBatchHandle(out PhysicsChainArenaHandle handle)
    {
        handle = _cpuBackendHandle;
        return _usePreparedCpuBackend && _cpuStepCount == 1 && handle.IsValid;
    }

    internal void MarkPreparedCpuBatchSolved()
    {
        _cpuBatchSolved = true;
        _lastSimulationProducedResults = true;
    }

    private bool ConsumeCpuBatchSolved()
    {
        bool solved = _cpuBatchSolved;
        _cpuBatchSolved = false;
        return solved;
    }
    private bool TryPrepareDataOrientedCpuSolve()
    {
        // Freeze-axis and virtual fallback colliders still use the compatibility
        // solver until those feature kernels have exact scalar parity.
        if (_cpuBackend is null || _cpuBackendWorld is null
            || FreezeAxis != EFreezeAxis.None
            || _fallbackCollidersForJobCount != 0
            || UpdateMode != EUpdateMode.Default)
            return false;

        ResolveSimulationLoopAndTimeScale(_deltaTime, out _cpuStepCount, out _cpuStepTime);
        if (_cpuStepCount <= 0)
            return false;

        PhysicsChainTemplate template = GetOrCreateRuntimeTemplate(_cpuBackendWorld);
        int treeCount = template.Trees.Length;
        int particleCount = template.Particles.Length;
        int colliderCount = _colliderSnapshotsForJobCount;
        bool requiresRegistration = !_cpuBackend.IsCurrent(_cpuBackendHandle)
            || _cpuBackendParticleVersion != _particlesVersion
            || _cpuTreeInputs.Length != treeCount
            || _cpuParticleInputs.Length != particleCount
            || (_cpuSharedColliderSet is null && _cpuColliders.Length != colliderCount)
            || (_cpuSharedColliderSet is not null
                && !PhysicsChainWorld.CpuSharedColliderShapesMatch(
                    _cpuSharedColliderSet,
                    _colliderSnapshotsForJob,
                    colliderCount))
            || _cpuRegisteredMirrorEnabled != _cpuTransformMirrorEnabled
            || _cpuRegisteredMirrorIntervalFrames != _cpuTransformMirrorIntervalFrames;

        if (requiresRegistration)
            RegisterCpuBackendInstance(template, treeCount, particleCount, colliderCount);
        ApplyCpuQualityPolicy();

        FillCpuDynamicInputs();
        PhysicsChainCpuInput input = new(
            _cpuStepTime,
            Speed: 1.0f,
            _objectScale,
            _weight,
            Gravity,
            Force,
            _objectMove,
            ResetState: 0u,
            CollisionEnabled: EffectiveQualityPolicy.CollisionEnabled ? 1u : 0u);
        if (!_cpuBackend.TryUpdateInput(_cpuBackendHandle, input)
            || !_cpuBackend.TryUpdateTreeInputs(_cpuBackendHandle, _cpuTreeInputs)
            || !_cpuBackend.TryUpdateParticleInputs(_cpuBackendHandle, _cpuParticleInputs)
            || (_cpuSharedColliderSet is null && !_cpuBackend.TryUpdateColliders(_cpuBackendHandle, _cpuColliders)))
            return false;

        if (_cpuBackendStateVersion != _particleStateVersion)
        {
            _cpuBackendStateVersion = _particleStateVersion;
            return _cpuBackend.TryReset(_cpuBackendHandle);
        }
        return true;
    }

    private void RegisterCpuBackendInstance(
        PhysicsChainTemplate template,
        int treeCount,
        int particleCount,
        int colliderCount)
    {
        if (_cpuBackend is null)
            return;
        if (_cpuBackendHandle.IsValid)
            _cpuBackend.Remove(_cpuBackendHandle);

        _cpuTreeInputs = new PhysicsChainCpuTreeInput[treeCount];
        _cpuParticleInputs = new PhysicsChainCpuParticleInput[particleCount];
        _cpuInitialStates = new PhysicsChainCpuState[particleCount];
        _cpuPublishedOutputs = new PhysicsChainCpuOutput[particleCount];
        _cpuSharedColliderSet = null;
        if (_cpuBackendWorld is not null && _colliderSnapshotsForJob is not null)
            _cpuBackendWorld.TryGetOrCreateCpuSharedColliderSet(
                Colliders ?? _effectiveColliders,
                _colliderSnapshotsForJob.AsSpan(0, colliderCount),
                out _cpuSharedColliderSet);
        _cpuColliders = _cpuSharedColliderSet is null
            ? new PhysicsChainCpuCollider[colliderCount]
            : [];
        FillCpuDynamicInputs();
        FillCpuInitialStates();
        PhysicsChainCpuInput input = new(
            _cpuStepTime,
            Speed: 1.0f,
            _objectScale,
            _weight,
            Gravity,
            Force,
            _objectMove,
            ResetState: 0u,
            CollisionEnabled: EffectiveQualityPolicy.CollisionEnabled ? 1u : 0u);
        PhysicsChainCpuConsumerFlags consumers = PhysicsChainCpuConsumerFlags.Palette
            | PhysicsChainCpuConsumerFlags.Bounds;
        if (_cpuTransformMirrorEnabled)
            consumers |= PhysicsChainCpuConsumerFlags.TransformMirror;

        PhysicsChainCpuMirrorPolicy mirrorPolicy = new(
            _cpuTransformMirrorEnabled,
            _cpuTransformMirrorIntervalFrames);
        _cpuBackendHandle = _cpuSharedColliderSet is not null
            ? _cpuBackend.RegisterShared(
                template, input, _cpuTreeInputs, _cpuParticleInputs,
                _cpuSharedColliderSet, _cpuInitialStates, consumers,
                mirrorPolicy: mirrorPolicy)
            : _cpuBackend.Register(
                template, input, _cpuTreeInputs, _cpuParticleInputs,
                _cpuInitialStates, _cpuColliders, consumers,
                mirrorPolicy: mirrorPolicy);
        _cpuBackendParticleVersion = _particlesVersion;
        _cpuBackendStateVersion = _particleStateVersion;
        _cpuRegisteredMirrorEnabled = _cpuTransformMirrorEnabled;
        _cpuRegisteredMirrorIntervalFrames = _cpuTransformMirrorIntervalFrames;
    }

    private void FillCpuDynamicInputs()
    {
        ParticleTree[]? trees = _particleTreesForJob;
        int flattenedIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTreesForJobCount; ++treeIndex)
        {
            ParticleTree tree = trees![treeIndex];
            _cpuTreeInputs[treeIndex] = new PhysicsChainCpuTreeInput(tree.RestGravity);
            List<Particle> particles = tree.Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
                _cpuParticleInputs[flattenedIndex++] = new PhysicsChainCpuParticleInput(particles[particleIndex].TransformLocalToWorldMatrix);
        }

        if (_cpuSharedColliderSet is not null)
            PhysicsChainWorld.TryUpdateCpuSharedColliderPoses(
                _cpuSharedColliderSet,
                _colliderSnapshotsForJob.AsSpan(0, _colliderSnapshotsForJobCount));
        else
            for (int colliderIndex = 0; colliderIndex < _colliderSnapshotsForJobCount; ++colliderIndex)
                _cpuColliders[colliderIndex] = ConvertCpuCollider(_colliderSnapshotsForJob![colliderIndex]);
    }

    private void FillCpuInitialStates()
    {
        ParticleTree[]? trees = _particleTreesForJob;
        int flattenedIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTreesForJobCount; ++treeIndex)
        {
            List<Particle> particles = trees![treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                _cpuInitialStates[flattenedIndex++] = new PhysicsChainCpuState
                {
                    Position = particle.Position,
                    PreviousPosition = particle.PrevPosition,
                    IsColliding = particle.IsColliding ? 1u : 0u,
                };
            }
        }
    }

    private bool SolvePreparedCpuBackend()
    {
        if (_cpuBackend is null)
            return false;
        _lastSimulationProducedResults = _cpuStepCount > 0;
        for (int step = 0; step < _cpuStepCount; ++step)
            if (!_cpuBackend.TryStep(_cpuBackendHandle))
                return false;
        return true;
    }

    private bool PublishPreparedCpuBackend()
    {
        _cpuMirrorPublishedThisFrame = false;
        if (!_cpuTransformMirrorEnabled)
            return true;
        if (_cpuBackend is null
            || !_cpuBackend.TryGetRenderOutput(_cpuBackendHandle, out PhysicsChainCpuRenderOutput renderOutput)
            || renderOutput.TransformMirrorAgeFrames != 0)
            return true;
        if (!_cpuBackend.TryCopyOutputs(_cpuBackendHandle, _cpuPublishedOutputs))
            return false;

        ParticleTree[]? trees = _particleTreesForJob;
        int flattenedIndex = 0;
        for (int treeIndex = 0; treeIndex < _particleTreesForJobCount; ++treeIndex)
        {
            List<Particle> particles = trees![treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                PhysicsChainCpuOutput output = _cpuPublishedOutputs[flattenedIndex++];
                Particle particle = particles[particleIndex];
                particle.PrevPosition = output.PreviousPosition;
                particle.Position = output.CurrentPosition;
                particle.IsColliding = output.IsColliding != 0u;
            }
        }
        _cpuMirrorPublishedThisFrame = true;
        return true;
    }

    private static PhysicsChainCpuCollider ConvertCpuCollider(in PhysicsChainColliderSnapshot collider)
        => collider.Kind switch
        {
            PhysicsChainColliderKind.Sphere => PhysicsChainCpuCollider.Sphere(collider.Center, collider.Radius),
            PhysicsChainColliderKind.Capsule => PhysicsChainCpuCollider.Capsule(collider.Center, collider.End, collider.Radius),
            PhysicsChainColliderKind.Box => PhysicsChainCpuCollider.Box(
                collider.Center, collider.AxisX, collider.AxisY, collider.AxisZ, collider.HalfExtents),
            PhysicsChainColliderKind.Plane => PhysicsChainCpuCollider.Plane(
                collider.PlaneNormal, collider.PlaneDistance, collider.Inside),
            _ => default,
        };

    private bool ConsumeCpuTransformMirrorPublished()
    {
        bool published = _cpuMirrorPublishedThisFrame;
        _cpuMirrorPublishedThisFrame = false;
        return published;
    }
}
