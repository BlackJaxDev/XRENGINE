using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using XREngine.Scene.Transforms;
using XREngine.Data.Geometry;

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
    private float[] _gpuParticleReach = [];
    private readonly List<float> _gpuTreeReach = [];
    private PhysicsChainGpuSpatialInput _gpuSpatialInput;
    private PhysicsChainGpuRestInputRange? _gpuRestInputCapture;
    private PhysicsChainRuntimeHandle _gpuRestInputHandle = PhysicsChainRuntimeHandle.Invalid;
    private PhysicsChainGpuRestInputRange? _lastPackedGpuRestInput;
    private PhysicsChainRuntimeHandle _lastPackedGpuRestHandle = PhysicsChainRuntimeHandle.Invalid;
    private ulong _lastPackedGpuRestMatrixGeneration;
    private int _lastPackedGpuRestSourceVersion;
    private bool _lastPackedWorldRest;
    private bool _gpuTransformsFromWorldRange;
    private bool _gpuStaticSpatialValid;
    private bool _gpuSeedSpatialValid;
    private Vector3 _gpuSeedMinimum;
    private Vector3 _gpuSeedMaximum;
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
        InvalidateReadbackSource();
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

        PrepareGpu(out ReadOnlySpan<Matrix4x4> worldRestMatrices);
        bool useWorldRest = !worldRestMatrices.IsEmpty;
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
        PrepareGpuDispatchDataCore(worldRestMatrices);
        if (observe)
            PhysicsChainWorld.RecordGpuInputPacking(Stopwatch.GetTimestamp() - packingStart);
        long dispatchStart = observe ? Stopwatch.GetTimestamp() : 0L;
        RuntimePhysicsChainRendering.Current.Execute(this, new PhysicsChainGpuDispatchSnapshot(
            CollectionsMarshal.AsSpan(_gpuParticles), CollectionsMarshal.AsSpan(_gpuParticleStatic),
            CollectionsMarshal.AsSpan(_gpuTrees),
            useWorldRest ? worldRestMatrices : CollectionsMarshal.AsSpan(_gpuTransforms),
            CollectionsMarshal.AsSpan(_gpuColliders), CollectionsMarshal.AsSpan(_gpuBones),
            _deltaTime, _objectScale, _weight, Force, Gravity, _objectMove, (int)FreezeAxis,
            loopCount, timeVar, _gpuExecutionGeneration, ++_gpuSubmissionId,
            _gpuStaticDataVersion, _gpuTreeDataVersion, _particleStateVersion,
            _gpuTransformSignature, _gpuColliderSignature,
            _gpuBoneSignature)
        {
            ReadbackSourceGeneration = ReadbackSourceGeneration,
            SpatialInput = _gpuSpatialInput with
            {
                IsValid = _gpuSpatialInput.IsValid && float.IsFinite(timeVar),
            },
        });
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
        // The CPU mirror does not replace retained solver seeds or their upload version.
        if (GpuSyncToBones)
            _hasPendingGpuBoneSync = true;
    }

    internal void AcceptGpuRestInputCapture(PhysicsChainGpuRestInputRange capture,
        PhysicsChainRuntimeHandle handle)
    {
        _gpuRestInputCapture = capture;
        _gpuRestInputHandle = handle;
    }

    /// <summary>Requests the rendering adapter to rebuild optional GPU skinning bindings.</summary>
    public void InvalidateGpuDrivenRenderers()
        => RuntimePhysicsChainRendering.Current.InvalidateGpuDrivenRenderers(this);

    private void RenderGpuDebug()
        => RuntimePhysicsChainRendering.Current.RenderDebug(this);

    private void PrepareGpuDispatchData()
        => PrepareGpuDispatchDataCore(default);

    private void PrepareGpuDispatchDataCore(ReadOnlySpan<Matrix4x4> worldRestMatrices)
    {
        bool useWorldRest = !worldRestMatrices.IsEmpty;
        HashCode colliderHash = new();
        bool rebuildStatic = !_gpuInputPrepared || _gpuStaticSourceVersion != _particlesVersion;
        bool rebuildParticles = !_gpuInputPrepared ||
            _gpuParticleSourceVersion != _particleStateVersion;
        PhysicsChainGpuRestInputRange? capture = _gpuRestInputCapture;
        if (useWorldRest && _lastPackedWorldRest && !rebuildStatic && !rebuildParticles &&
            (Colliders is null || Colliders.Count == 0) &&
            (_effectiveColliders is null || _effectiveColliders.Count == 0) &&
            _gpuColliders.Count == 0 && capture is not null &&
            _gpuRestInputHandle == _runtimeHandle && capture.SourceVersion == _particlesVersion &&
            capture.Count == worldRestMatrices.Length && capture.Count == _gpuParticles.Count &&
            capture.TreeCount == _particleTrees.Count && capture.TreeCount == _gpuTrees.Count &&
            capture.TreeCount == _gpuTreeReach.Count)
        {
            PrepareGpuDispatchDataFromWorld(capture);
            return;
        }
        bool treeChanged = rebuildStatic;
        bool staticSpatialValid = true;
        bool seedSpatialValid = true;
        if (rebuildParticles)
            _gpuParticles.Clear();
        if (rebuildStatic)
        {
            _gpuParticleStatic.Clear();
            _gpuTrees.Clear();
            _gpuBones.Clear();
            _gpuTreeReach.Clear();
            int totalParticles = 0;
            for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
                totalParticles = checked(totalParticles + _particleTrees[treeIndex].Particles.Count);
            if (_gpuParticleReach.Length < totalParticles)
                Array.Resize(ref _gpuParticleReach, totalParticles);
        }
        int previousTransformCount = _gpuTransforms.Count;
        // The aggregate route does not update the component matrix copy. A
        // full pack after that route must publish a new upload witness even
        // when the authored matrices match the older component copy.
        bool transformsChanged = rebuildStatic || _gpuTransformsFromWorldRange;
        _gpuColliders.Clear();
        Vector3 rootMinimum = new(float.PositiveInfinity);
        Vector3 rootMaximum = new(float.NegativeInfinity);
        Vector3 seedMinimum = new(float.PositiveInfinity);
        Vector3 seedMaximum = new(float.NegativeInfinity);
        float maximumStretch = 0.0f;
        bool spatialValid = true;
        int particleOffset = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            Vector3 anchorMinimum = new(float.PositiveInfinity);
            Vector3 anchorMaximum = new(float.NegativeInfinity);
            float treeReach = rebuildStatic ? 0.0f : _gpuTreeReach[treeIndex];
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
                Vector3 localPosition = useWorldRest && particle.Transform is not null
                    ? particle.ParentIndex < 0
                        ? particle.Transform.FrameState.Translation
                        : particle.InitLocalPosition
                    : particle.TransformLocalPosition;
                bool particleStaticValid = IsFiniteSpatialVector(localPosition) &&
                    float.IsFinite(particle.Damping) && float.IsFinite(particle.Elasticity) &&
                    float.IsFinite(particle.Stiffness) && float.IsFinite(particle.Inert) &&
                    float.IsFinite(particle.Friction) && float.IsFinite(particle.Radius) &&
                    float.IsFinite(particle.SegmentLength) && particle.SegmentLength >= 0.0f;
                spatialValid &= particleStaticValid;
                staticSpatialValid &= (particle.ParentIndex < 0 ||
                    IsFiniteSpatialVector(localPosition)) &&
                    float.IsFinite(particle.Damping) && float.IsFinite(particle.Elasticity) &&
                    float.IsFinite(particle.Stiffness) && float.IsFinite(particle.Inert) &&
                    float.IsFinite(particle.Friction) && float.IsFinite(particle.Radius) &&
                    float.IsFinite(particle.SegmentLength) && particle.SegmentLength >= 0.0f;
                if (rebuildParticles)
                    _gpuParticles.Add(new(particle.Position, particle.PrevPosition, particle.IsColliding ? 1 : 0, particle.PreviousPhysicsPosition));
                if (rebuildStatic)
                {
                    _gpuParticleStatic.Add(new(localPosition,
                        particle.ParentIndex >= 0 ? particle.ParentIndex + particleOffset : -1,
                        particle.Damping, particle.Elasticity, particle.Stiffness, particle.Inert, particle.Friction,
                        particle.Radius, particle.SegmentLength, treeIndex));
                    PhysicsChainGpuBone bone = new(particle.Transform,
                        particle.ParentIndex >= 0 ? particle.ParentIndex + particleOffset : -1,
                        particle.Transform is not null ? particle.InitLocalPosition : particle.EndOffset);
                    _gpuBones.Add(bone);
                    float length = particle.SegmentLength;
                    bool lengthValid = float.IsFinite(length) && length >= 0.0f;
                    spatialValid &= lengthValid;
                    staticSpatialValid &= lengthValid;
                    float reach = particle.ParentIndex >= 0 && particle.ParentIndex < particleIndex
                        ? _gpuParticleReach[particleOffset + particle.ParentIndex] + MathF.Max(length, 0.0001f)
                        : 0.0f;
                    _gpuParticleReach[particleOffset + particleIndex] = reach;
                    treeReach = MathF.Max(treeReach, reach);
                }
                // Root motion uses its transform matrix. Only child local offsets
                // are read from the static template by the GPU solver.
                Matrix4x4 transformMatrix = useWorldRest
                    ? worldRestMatrices[particleOffset + particleIndex]
                    : particle.Transform is not null
                    ? particle.TransformLocalToWorldMatrix
                    : particle.ParentIndex >= 0
                        ? tree.Particles[particle.ParentIndex].TransformLocalToWorldMatrix
                        : Matrix4x4.Identity;
                int transformIndex = particleOffset + particleIndex;
                if (transformIndex < previousTransformCount)
                {
                    transformsChanged |= _gpuTransforms[transformIndex] != transformMatrix;
                    _gpuTransforms[transformIndex] = transformMatrix;
                }
                else
                {
                    transformsChanged = true;
                    _gpuTransforms.Add(transformMatrix);
                }
                Vector3 translation = transformMatrix.Translation;
                spatialValid &= IsFiniteSpatialVector(translation) &&
                    transformMatrix.M14 == 0.0f && transformMatrix.M24 == 0.0f &&
                    transformMatrix.M34 == 0.0f && transformMatrix.M44 == 1.0f;
                float stretch = PhysicsChainGpuSpatialInput.MaximumLinearStretch(in transformMatrix);
                spatialValid &= float.IsFinite(stretch);
                maximumStretch = MathF.Max(maximumStretch, stretch);
                if (particle.ParentIndex < 0 || particle.ParentIndex >= particleIndex)
                {
                    anchorMinimum = Vector3.Min(anchorMinimum, translation);
                    anchorMaximum = Vector3.Max(anchorMaximum, translation);
                }
                PhysicsChainGpuParticle seed = _gpuParticles[transformIndex];
                bool seedValid = IsFiniteSpatialVector(seed.Position) &&
                    IsFiniteSpatialVector(seed.PrevPosition) &&
                    IsFiniteSpatialVector(seed.PreviousPhysicsPosition);
                spatialValid &= seedValid;
                seedSpatialValid &= seedValid;
                seedMinimum = Vector3.Min(seedMinimum, seed.Position);
                seedMaximum = Vector3.Max(seedMaximum, seed.Position);
            }
            if (rebuildStatic)
                _gpuTreeReach.Add(treeReach);
            Vector3 reachPadding = new(MathF.BitIncrement(treeReach + 0.0001f * MathF.Max(1.0f, treeReach)));
            rootMinimum = Vector3.Min(rootMinimum, anchorMinimum - reachPadding);
            rootMaximum = Vector3.Max(rootMaximum, anchorMaximum + reachPadding);
            particleOffset += tree.Particles.Count;
        }
        if (_gpuTransforms.Count > particleOffset)
        {
            transformsChanged = true;
            _gpuTransforms.RemoveRange(particleOffset, _gpuTransforms.Count - particleOffset);
        }
        if (transformsChanged)
            _gpuTransformSignature = checked(_gpuTransformSignature + 1);
        spatialValid &= particleOffset > 0 && IsFiniteSpatialVector(rootMinimum) &&
            IsFiniteSpatialVector(rootMaximum) && IsFiniteSpatialVector(seedMinimum) &&
            IsFiniteSpatialVector(seedMaximum);
        if (rebuildStatic)
            unchecked { ++_gpuStaticDataVersion; }
        if (treeChanged)
            unchecked { ++_gpuTreeDataVersion; }
        if (rebuildStatic)
            _gpuBoneSignature = checked(_gpuBoneSignature + 1);
        _gpuStaticSourceVersion = _particlesVersion;
        _gpuParticleSourceVersion = _particleStateVersion;
        _gpuInputPrepared = true;
        _gpuStaticSpatialValid = staticSpatialValid;
        _gpuSeedSpatialValid = seedSpatialValid;
        _gpuSeedMinimum = seedMinimum;
        _gpuSeedMaximum = seedMaximum;
        _gpuTransformsFromWorldRange = false;
        _lastPackedWorldRest = useWorldRest;
        if (useWorldRest && capture is not null)
        {
            _lastPackedGpuRestInput = capture;
            _lastPackedGpuRestHandle = _gpuRestInputHandle;
            _lastPackedGpuRestMatrixGeneration = capture.MatrixGeneration;
            _lastPackedGpuRestSourceVersion = capture.SourceVersion;
        }

        if (_effectiveColliders is not null)
            for (int i = 0; i < _effectiveColliders.Count; ++i)
                AppendGpuCollider(_effectiveColliders[i]);

        for (int i = 0; i < _gpuColliders.Count; ++i)
        {
            PhysicsChainGpuCollider collider = _gpuColliders[i];
            spatialValid &= IsFiniteSpatialVector(new Vector3(collider.Center.X, collider.Center.Y, collider.Center.Z)) &&
                float.IsFinite(collider.Center.W) &&
                IsFiniteSpatialVector(new Vector3(collider.Params.X, collider.Params.Y, collider.Params.Z)) &&
                float.IsFinite(collider.Params.W) &&
                IsFiniteSpatialVector(new Vector3(collider.Orientation.X, collider.Orientation.Y, collider.Orientation.Z)) &&
                float.IsFinite(collider.Orientation.W);
            colliderHash.Add(collider.Center);
            colliderHash.Add(collider.Params);
            colliderHash.Add(collider.Orientation);
            colliderHash.Add(collider.Type);
        }

        _gpuColliderSignature = colliderHash.ToHashCode();
        spatialValid &= float.IsFinite(_deltaTime) && float.IsFinite(_objectScale) &&
            float.IsFinite(_weight) && IsFiniteSpatialVector(Force) && IsFiniteSpatialVector(Gravity) &&
            IsFiniteSpatialVector(_objectMove);
        _gpuSpatialInput = new(new(rootMinimum, rootMaximum), new(seedMinimum, seedMaximum),
            maximumStretch, spatialValid);
    }

    private void PrepareGpuDispatchDataFromWorld(PhysicsChainGpuRestInputRange capture)
    {
        bool treeChanged = false;
        Vector3 rootMinimum = new(float.PositiveInfinity);
        Vector3 rootMaximum = new(float.NegativeInfinity);
        for (int treeIndex = 0; treeIndex < capture.TreeCount; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            if (_gpuTrees[treeIndex].RestGravity != tree.RestGravity)
            {
                _gpuTrees[treeIndex] = _gpuTrees[treeIndex] with { RestGravity = tree.RestGravity };
                treeChanged = true;
            }

            float treeReach = _gpuTreeReach[treeIndex];
            Vector3 reachPadding = new(MathF.BitIncrement(treeReach +
                0.0001f * MathF.Max(1.0f, treeReach)));
            rootMinimum = Vector3.Min(rootMinimum,
                capture.TreeAnchorMinimum[treeIndex] - reachPadding);
            rootMaximum = Vector3.Max(rootMaximum,
                capture.TreeAnchorMaximum[treeIndex] + reachPadding);
        }

        if (treeChanged)
            unchecked { ++_gpuTreeDataVersion; }
        if (!_lastPackedWorldRest || !ReferenceEquals(_lastPackedGpuRestInput, capture) ||
            _lastPackedGpuRestHandle != _gpuRestInputHandle ||
            _lastPackedGpuRestSourceVersion != capture.SourceVersion ||
            _lastPackedGpuRestMatrixGeneration != capture.MatrixGeneration)
            _gpuTransformSignature = checked(_gpuTransformSignature + 1);
        _lastPackedWorldRest = true;
        _gpuTransformsFromWorldRange = true;
        _lastPackedGpuRestInput = capture;
        _lastPackedGpuRestHandle = _gpuRestInputHandle;
        _lastPackedGpuRestSourceVersion = capture.SourceVersion;
        _lastPackedGpuRestMatrixGeneration = capture.MatrixGeneration;

        bool spatialValid = _gpuStaticSpatialValid && _gpuSeedSpatialValid &&
            capture.SpatialValid && capture.Count > 0 &&
            IsFiniteSpatialVector(rootMinimum) && IsFiniteSpatialVector(rootMaximum) &&
            IsFiniteSpatialVector(_gpuSeedMinimum) && IsFiniteSpatialVector(_gpuSeedMaximum) &&
            float.IsFinite(_deltaTime) && float.IsFinite(_objectScale) &&
            float.IsFinite(_weight) && IsFiniteSpatialVector(Force) &&
            IsFiniteSpatialVector(Gravity) && IsFiniteSpatialVector(_objectMove);
        _gpuSpatialInput = new(new(rootMinimum, rootMaximum),
            new(_gpuSeedMinimum, _gpuSeedMaximum), capture.MaximumStretch, spatialValid);
    }

    private static bool IsFiniteSpatialVector(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

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
            _gpuColliders.Add(new(
                new Vector4(start, capsule.Radius),
                new Vector4(end, lengthSquared > 1e-8f ? 1.0f / lengthSquared : 0.0f),
                new Vector4(end - start, -65536.0f),
                1));
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
