using XREngine.Extensions;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Animation;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Execution;
using XREngine.Scene.Transforms;
using XREngine.Timers;
using YamlDotNet.Serialization;

namespace XREngine.Components;

public partial class PhysicsChainComponent : XRComponent
{
    private static readonly TimeSpan FaultLogInterval = TimeSpan.FromSeconds(2);

    private bool _isSimulating;
    private bool _isValidating;
    private bool _rebuildQueued;
    private int _worldMutationDepth;

    internal bool IsWorldMutationPending => Volatile.Read(ref _worldMutationDepth) != 0;

    internal void BeginWorldMutation()
    {
        using (_runtimeBindingSync.EnterScope())
        {
            if (_worldMutationDepth == 0)
                InvalidateReadbackSource();
            Interlocked.Increment(ref _worldMutationDepth);
        }
    }

    internal void EndWorldMutation()
    {
        using (_runtimeBindingSync.EnterScope())
            Interlocked.Decrement(ref _worldMutationDepth);
    }
    private int _particlesVersion;
    private int _particleStateVersion;

    private static string FormatRoot(Transform? root)
        => root?.ToString() ?? "<null>";

    private static void LogFault(string key, string message)
    {
        if (!Debug.ShouldLogEvery(key, FaultLogInterval))
            return;

        Debug.PhysicsWarning($"[PhysicsChain] {message}");
    }

    private void QueueRebuild(string reason)
    {
        _rebuildQueued = true;
        LogFault($"QueueRebuild:{reason}", $"Particle rebuild queued during simulation. Reason={reason}.");
    }

    protected override void OnComponentActivated()
    {
        using var mutation = PhysicsChainWorld.BeginComponentMutation(this);
        base.OnComponentActivated();
        ActivateGpuExecutionMode();
        PhysicsChainWorld.BindPendingRuntimeGraph(this);
        SetupParticles();
        PhysicsChainWorld.Register(this);
        ResetParticlesPosition();
        InitializeRootBoneTracking();
        OnValidate();
    }
    protected override void OnComponentDeactivated()
    {
        using var mutation = PhysicsChainWorld.BeginComponentMutation(this);
        PhysicsChainWorld.RetireForComponentMutation(this);
        PhysicsChainWorld.Unregister(this);
        DeactivateGpuExecutionMode();
        base.OnComponentDeactivated();
        InitTransforms();
        _preUpdateCount = 0;
        _cpuBackendWorld?.ResetSimulationClock(_runtimeHandle, this);
    }

    private void FixedUpdate()
    {
        if (UpdateMode == EUpdateMode.FixedUpdate)
            PreUpdate();
    }

    private void Update()
    {
        if (UpdateMode != EUpdateMode.FixedUpdate)
            PreUpdate();

        if (UpdateMode == EUpdateMode.FixedUpdate)
            _cpuBackendWorld?.AdvanceFixedRenderClock(
                _runtimeHandle, this, RuntimeTimingServices.Current.UpdateDeltaTicks);
        
    }

    internal void WorldFixedTick()
        => FixedUpdate();

    internal void WorldUpdateTick()
        => Update();

    internal static void AdvancePreparedColliderFrame()
    {
        unchecked
        {
            ++_prepareFrame;
        }
    }

    internal PhysicsChainWorld? CaptureRuntimeOwner()
    {
        using (_runtimeBindingSync.EnterScope())
            return _runtimeOwnerWorld;
    }

    internal void CaptureRuntimeBinding(out PhysicsChainWorld? owner, out PhysicsChainRuntimeHandle handle)
    {
        using (_runtimeBindingSync.EnterScope())
        {
            owner = _runtimeOwnerWorld;
            handle = _runtimeHandle;
        }
    }

    /// <summary>Identifies the particle layout and reset state used by delayed GPU snapshots.</summary>
    [YamlIgnore]
    public long ReadbackSourceGeneration => RuntimeGraph.ReadbackSourceGeneration;

    internal void InvalidateReadbackSource() => RuntimeGraph.InvalidateReadbackSource();

    internal bool MatchesReadbackSource(PhysicsChainWorld world, PhysicsChainRuntimeHandle handle, long generation)
    {
        PhysicsChainRuntimeGraph graph = RuntimeGraph;
        PhysicsChainReadbackBinding? binding = graph.ReadbackBinding;
        return !IsWorldMutationPending && !IsDestroyed && binding is not null
            && ReferenceEquals(binding.World, world) && binding.Handle == handle
            && graph.ReadbackSourceGeneration == generation;
    }

    internal bool TryBindRuntimeOwner(PhysicsChainWorld world, PhysicsChainRuntimeHandle handle)
    {
        using (_runtimeBindingSync.EnterScope())
        {
            if (_runtimeOwnerWorld is not null && !ReferenceEquals(_runtimeOwnerWorld, world))
                return false;
            _runtimeOwnerWorld = world;
            RuntimeGraph.BindReadbackSource(world, handle);
            SetField(ref _runtimeHandle, handle, nameof(RuntimeHandle));
            return true;
        }
    }

    internal bool TryReleaseRuntimeOwner(PhysicsChainWorld world, PhysicsChainRuntimeHandle handle)
    {
        using (_runtimeBindingSync.EnterScope())
        {
            if (!ReferenceEquals(_runtimeOwnerWorld, world) || _runtimeHandle != handle)
                return false;
            DetachCpuBackend(world);
            RuntimeGraph.UnbindReadbackSource();
            _runtimeOwnerWorld = null;
            SetField(ref _runtimeHandle, PhysicsChainRuntimeHandle.Invalid, nameof(RuntimeHandle));
            return true;
        }
    }

    internal void SetEffectiveQualityTier(PhysicsChainQualityTier tier)
    {
        float previousRate = ResolveEffectiveUpdateRate();
        float progress = _cpuBackendWorld?.CaptureCadenceProgress(
            _runtimeHandle, this, previousRate, 0.0f) ?? 0.0f;
        if (!SetField(ref _effectiveQualityTier, tier, nameof(EffectiveQualityTier)))
            return;
        float nextRate = ResolveEffectiveUpdateRate();
        _cpuBackendWorld?.SetCadenceProgress(_runtimeHandle, this, progress, nextRate);
        ApplyCpuQualityPolicy();

        if (tier == PhysicsChainQualityTier.Sleep)
            HoldSleepingOutputHistory();
        else
            Wake(PhysicsChainWakeReason.QualityPolicyChanged);
    }

    /// <summary>
    /// Wakes an automatically sleeping chain after gameplay, root, collider,
    /// or authoring input changes.
    /// </summary>
    public void Wake()
        => Wake(PhysicsChainWakeReason.ExplicitRequest);

    /// <summary>Wakes the chain for a gameplay force, impulse, or collision event.</summary>
    public void NotifyExternalEvent()
        => Wake(PhysicsChainWakeReason.ForceOrEventInput);

    /// <summary>
    /// Marks the chain as visible or otherwise consumed. Repeated calls keep
    /// it active for the configured grace period without allocating state.
    /// </summary>
    public void NotifyVisibleOrUsed()
    {
        _recentUseFramesRemaining = Math.Max(_recentUseFramesRemaining, _sleepRecentUseFrameCount);
        _recentInteractionQualityFramesRemaining = Math.Max(
            _recentInteractionQualityFramesRemaining,
            _recentInteractionQualityFrameCount);
        Wake(PhysicsChainWakeReason.VisibilityOrUse);
    }

    /// <summary>Updates renderer visibility used by automatic offscreen policy.</summary>
    public void SetRuntimeVisibility(bool visible)
    {
        if (!SetField(ref _runtimeVisible, visible, nameof(RuntimeVisible)))
            return;
        _offscreenQualityFrames = 0;
        if (visible)
        {
            _recentInteractionQualityFramesRemaining = Math.Max(
                _recentInteractionQualityFramesRemaining,
                _recentInteractionQualityFrameCount);
            Wake(PhysicsChainWakeReason.RelevanceChanged);
        }
    }

    /// <summary>
    /// Wakes an automatically sleeping chain and records the observable cause.
    /// </summary>
    public void Wake(PhysicsChainWakeReason reason)
    {
        if (_isRuntimeSleeping)
            ++_wakeCount;

        _isRuntimeSleeping = false;
        _quietSimulationFrames = 0;
        _lastWakeReason = reason;
    }

    internal int EstimatedWorldWork
    {
        get
        {
            int particleCount = 0;
            for (int i = 0; i < _particleTrees.Count; ++i)
                particleCount += _particleTrees[i].Particles.Count;
            int colliderCount = _colliderSnapshotsForJobCount + _fallbackCollidersForJobCount;
            return Math.Max(particleCount * Math.Max(colliderCount, 1), 1);
        }
    }

    /// <summary>
    /// Executes serial preparation for the world late tick. Returns true when
    /// this component has prepared CPU work for the shared parallel solve.
    /// </summary>
    internal bool BeginWorldLateTick()
    {
        if (_preUpdateCount == 0)
        {
            ApplyPendingGpuBoneSync();
            if (ShouldRefreshInterpolatedRenderPose())
                ApplyInterpolatedRenderPose();
            return false;
        }

        _isSimulating = true;
        SetWeight(BlendWeight);

        if (ShouldRemainSleeping())
        {
            CompleteWorldLateTick();
            return false;
        }

        if (UseGPU)
        {
            ExecuteGpuLateUpdate();
            if (GpuSyncToBones)
            {
                if (_hasPendingGpuBoneSync)
                    ApplyPendingGpuBoneSync();
                else
                    ApplyCurrentParticleTransforms();
            }

            CompleteWorldLateTick();
            return false;
        }
        else if (Multithread)
        {
            CheckDistance();
            if (IsNeedUpdate())
            {
                Prepare();
                FinalizeWorldLateTickParallelPreparation();
                return true;
            }
        }
        else
        {
            CheckDistance();
            if (IsNeedUpdate())
            {
                Prepare();
                UpdateParticles();
                EvaluateRuntimeSleep();
                ApplyCurrentParticleTransforms(newSimulationResults: _lastSimulationProducedResults);
            }
        }

        CompleteWorldLateTick();
        return false;
    }

    internal bool CanPrepareWorldLateTickInputsInParallel
        => _preUpdateCount != 0 && !UseGPU && Multithread;

    internal bool BeginWorldLateTickParallelPreparation()
    {
        _isSimulating = true;
        SetWeight(BlendWeight);
        if (ShouldRemainSleeping())
        {
            CompleteWorldLateTick();
            return false;
        }

        CheckDistance();
        if (!IsNeedUpdate())
        {
            CompleteWorldLateTick();
            return false;
        }

        Prepare();
        return true;
    }

    internal void FinalizeWorldLateTickParallelPreparation()
        => _usePreparedCpuBackend = TryPrepareDataOrientedCpuSolve();

    internal void PrepareWorldCollidersForParallelInputGather()
    {
        List<PhysicsChainColliderBase>? colliders = Colliders;
        if (colliders is null)
            return;

        for (int colliderIndex = 0; colliderIndex < colliders.Count; ++colliderIndex)
        {
            PhysicsChainColliderBase collider = colliders[colliderIndex];
            if (collider is null || !collider.IsActiveInHierarchy || collider.PrepareFrame == _prepareFrame)
                continue;

            collider.Prepare();
            collider.PrepareFrame = _prepareFrame;
        }
    }

    internal void SolveWorldLateTick()
    {
        if (ConsumeCpuBatchSolved())
            return;

        if (!_usePreparedCpuBackend || !SolvePreparedCpuBackend())
        {
            _usePreparedCpuBackend = false;
            UpdateParticles();
        }
    }

    internal void PublishWorldLateTick()
    {
        EvaluateRuntimeSleep();
        bool publishHierarchy = !_usePreparedCpuBackend;
        if (_usePreparedCpuBackend)
        {
            if (!PublishPreparedCpuBackend())
                _usePreparedCpuBackend = false;
            else
                publishHierarchy = ConsumeCpuTransformMirrorPublished();
        }
        if (publishHierarchy)
            ApplyCurrentParticleTransforms(newSimulationResults: _lastSimulationProducedResults);
        _usePreparedCpuBackend = false;
        CompleteWorldLateTick();
    }

    internal void AbortWorldLateTick()
    {
        _usePreparedCpuBackend = false;
        _preUpdateCount = 0;
        _isSimulating = false;
    }

    private void CompleteWorldLateTick()
    {
        _preUpdateCount = 0;
        _isSimulating = false;
        ApplyPendingGpuExecutionReconfigure();

        if (_rebuildQueued)
        {
            _rebuildQueued = false;
            SetupParticles();
            ResetParticlesPosition();
        }
    }

    private void Prepare(bool snapshotForCpuJobs = true)
        => PrepareCore(snapshotForCpuJobs, attemptWorldGpuRest: false, out _);

    private void PrepareGpu(out ReadOnlySpan<Matrix4x4> gpuRestMatrices)
        => PrepareCore(snapshotForCpuJobs: false, attemptWorldGpuRest: true,
            out gpuRestMatrices);

    private void PrepareCore(
        bool snapshotForCpuJobs,
        bool attemptWorldGpuRest,
        out ReadOnlySpan<Matrix4x4> gpuRestMatrices)
    {
        bool observeGpuPrepare = UseGPU && RuntimeWorldTickTelemetry.Enabled;
        long restPoseTicks = 0L;
        _deltaTime = RuntimeTimingServices.Current.UpdateDeltaSeconds;
        switch (UpdateMode)
        {
            case EUpdateMode.Undilated:
                _deltaTime = RuntimeTimingServices.Current.UpdateDeltaSeconds;
                break;
            case EUpdateMode.FixedUpdate:
                _deltaTime = RuntimeTimingServices.Current.FixedDeltaSeconds * _preUpdateCount;
                break;
        }

        var translation = Transform.WorldTranslation;
        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        
        // Calculate base object movement
        Vector3 rawObjectMove = translation - _objectPrevPosition;
        _objectPrevPosition = translation;

        // Handle root bone relative movement if configured
        if (_rootBone is not null && _rootInertia > 0.0f)
        {
            _rootBone.RecalculateMatrices();
            Vector3 rootBonePos = _rootBone.WorldTranslation;
            Vector3 rootBoneMove = rootBonePos - _rootBonePrevPosition;
            _rootBonePrevPosition = rootBonePos;

            // Blend between world-space movement and root-relative movement
            // At RootInertia=1, we subtract the root bone's movement from the chain's perception of movement
            // This makes the chain move "with" the root bone rather than lagging behind
            rawObjectMove -= rootBoneMove * _rootInertia;
        }

        // Apply velocity smoothing to reduce jitter at high velocities
        if (_velocitySmoothing > 0.0f)
        {
            // Exponential moving average for smooth velocity
            // Higher smoothing = more dampened response
            float smoothFactor = 1.0f - _velocitySmoothing * 0.9f; // Map 0-1 to 1-0.1
            _smoothedObjectMove = Vector3.Lerp(_smoothedObjectMove, rawObjectMove, smoothFactor);
            _objectMove = _smoothedObjectMove;
        }
        else
        {
            _objectMove = rawObjectMove;
            _smoothedObjectMove = rawObjectMove;
        }

        gpuRestMatrices = default;
        bool useWorldGpuRest = attemptWorldGpuRest && _cpuBackendWorld is not null &&
            _cpuBackendWorld.TryConsumeGpuRestInputs(_runtimeHandle, this, out gpuRestMatrices);
        int gpuRestOffset = 0;
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            if (pt.Particles is not { Count: > 0 })
                continue;

            if (useWorldGpuRest)
            {
                pt.RestGravity = Vector3.TransformNormal(
                    pt.LocalGravity, gpuRestMatrices[gpuRestOffset]);
                gpuRestOffset += pt.Particles.Count;
                UpdateRootParentMotion(pt);
                continue;
            }

            // The rest pose is composed from the initial local poses and is never written to the
            // live bones. The collect thread publishes render matrices while this thread
            // prepares and solves, so a rest pose in the live bones can render for one frame.
            long restPoseStart = observeGpuPrepare ? Stopwatch.GetTimestamp() : 0L;
            ReadRestPose(pt);
            if (observeGpuPrepare)
                restPoseTicks += Stopwatch.GetTimestamp() - restPoseStart;

            UpdateRootParentMotion(pt);
        }

        // The rest-pose read composes matrices and reads no hierarchy, so it owns all of the time.
        if (observeGpuPrepare)
            PhysicsChainWorld.RecordGpuPrepareDetails(restPoseTicks, 0L, 0L);

        _effectiveColliders?.Clear();

        if (Colliders is not null)
        {
            for (int i = 0; i < Colliders.Count; ++i)
            {
                PhysicsChainColliderBase c = Colliders[i];
                if (c is null || !c.IsActiveInHierarchy)
                    continue;

                (_effectiveColliders ??= []).Add(c);
                if (c.PrepareFrame == _prepareFrame)
                    continue;

                c.Prepare();
                c.PrepareFrame = _prepareFrame;
            }
        }

        // Snapshot collections into arrays so job worker threads iterate
        // stable, immutable data instead of the mutable lists above.
        if (snapshotForCpuJobs)
            SnapshotForJobs();
    }

    /// <summary>
    /// Copies <see cref="_particleTrees"/> and <see cref="_effectiveColliders"/>
    /// into pre-allocated arrays that job worker threads can safely iterate.
    /// Only allocates when the element count grows.
    /// </summary>
    private void SnapshotForJobs()
    {
        // Particle trees
        int treeCount = _particleTrees.Count;
        if (_particleTreesForJob is null || _particleTreesForJob.Length < treeCount)
            _particleTreesForJob = new ParticleTree[Math.Max(treeCount, 1)];
        for (int i = 0; i < treeCount; ++i)
            _particleTreesForJob[i] = _particleTrees[i];
        _particleTreesForJobCount = treeCount;

        // Effective colliders. Common shapes become immutable value snapshots
        // so worker threads avoid component virtual dispatch and transform
        // reads. Custom/legacy shapes retain an explicit fallback lane.
        int colCount = _effectiveColliders?.Count ?? 0;
        if (_colliderSnapshotsForJob is null || _colliderSnapshotsForJob.Length < colCount)
            _colliderSnapshotsForJob = new PhysicsChainColliderSnapshot[Math.Max(colCount, 4)];
        if (_fallbackCollidersForJob is null || _fallbackCollidersForJob.Length < colCount)
            _fallbackCollidersForJob = new PhysicsChainColliderBase[Math.Max(colCount, 4)];

        int snapshotCount = 0;
        int fallbackCount = 0;
        for (int i = 0; i < colCount; ++i)
        {
            PhysicsChainColliderBase collider = _effectiveColliders![i];
            if (TryCreateColliderSnapshot(collider, out PhysicsChainColliderSnapshot snapshot))
                _colliderSnapshotsForJob[snapshotCount++] = snapshot;
            else
                _fallbackCollidersForJob[fallbackCount++] = collider;
        }
        _colliderSnapshotsForJobCount = snapshotCount;
        _fallbackCollidersForJobCount = fallbackCount;
    }

    private static bool TryCreateColliderSnapshot(PhysicsChainColliderBase collider, out PhysicsChainColliderSnapshot snapshot)
    {
        if (collider is PhysicsChainSphereCollider sphere)
        {
            TransformBase? transform = sphere.ColliderTransform ?? sphere.DefaultTransform;
            if (transform is not null)
            {
                snapshot = new PhysicsChainColliderSnapshot
                {
                    Kind = PhysicsChainColliderKind.Sphere,
                    ParticleContactOnly = sphere.ParticleContactOnly,
                    Center = transform.WorldTranslation,
                    Radius = MathF.Max(sphere.Radius, 0.0f),
                };
                return true;
            }
        }
        else if (collider is PhysicsChainCapsuleCollider capsule)
        {
            TransformBase? transform = capsule.ColliderTransform ?? capsule.DefaultTransform;
            if (transform is not null)
            {
                Vector3 center = transform.WorldTranslation;
                Vector3 halfAxis = transform.WorldUp * (capsule.Height * 0.5f);
                Vector3 start = center - halfAxis;
                Vector3 end = center + halfAxis;
                float lengthSquared = Vector3.DistanceSquared(start, end);
                snapshot = new PhysicsChainColliderSnapshot
                {
                    Kind = PhysicsChainColliderKind.Capsule,
                    ParticleContactOnly = capsule.ParticleContactOnly,
                    Center = start,
                    End = end,
                    Radius = MathF.Max(capsule.Radius, 0.0f),
                    InverseLengthSquared = lengthSquared > 1e-8f ? 1.0f / lengthSquared : 0.0f,
                };
                return true;
            }
        }
        else if (collider is PhysicsChainBoxCollider box)
        {
            TransformBase? transform = box.ColliderTransform ?? box.DefaultTransform;
            if (transform is not null)
            {
                Vector3 scale = Vector3.Abs(transform.LossyWorldScale);
                snapshot = new PhysicsChainColliderSnapshot
                {
                    Kind = PhysicsChainColliderKind.Box,
                    ParticleContactOnly = box.ParticleContactOnly,
                    Center = transform.WorldTranslation,
                    AxisX = Vector3.Normalize(transform.WorldRight),
                    AxisY = Vector3.Normalize(transform.WorldUp),
                    AxisZ = Vector3.Normalize(transform.WorldForward),
                    HalfExtents = Vector3.Abs(box.Size) * scale * 0.5f,
                };
                return true;
            }
        }
        else if (collider is PhysicsChainPlaneCollider plane)
        {
            snapshot = new PhysicsChainColliderSnapshot
            {
                Kind = PhysicsChainColliderKind.Plane,
                ParticleContactOnly = plane.ParticleContactOnly,
                PlaneNormal = plane._plane.Normal,
                PlaneDistance = plane._plane.D,
                Inside = plane._bound == PhysicsChainColliderBase.EBound.Inside,
            };
            return true;
        }

        snapshot = default;
        return false;
    }

    private bool IsNeedUpdate()
        => _weight > 0 && !(DistantDisable && _distantDisabled);

    private void PreUpdate()
    {
        ++_preUpdateCount;
    }

    private bool ShouldRemainSleeping()
    {
        if (_effectiveQualityTier == PhysicsChainQualityTier.Sleep)
            return true;
        if (!_isRuntimeSleeping)
            return false;

        Vector3 rootPosition = Transform.WorldTranslation;
        float rootDisplacementSquared = Vector3.DistanceSquared(rootPosition, _sleepRootPosition);
        Vector3 rootVelocity = rootPosition - _sleepLastRootPosition;
        _sleepLastRootPosition = rootPosition;
        Vector3 rootAcceleration = rootVelocity - _sleepRootStep;
        _sleepRootStep = rootVelocity;
        float rootAccelerationSquared = rootAcceleration.LengthSquared();
        float externalForceSquared = Force.LengthSquared();
        float teleportDistance = MathF.Max(_sleepTeleportDistance, 1e-5f);
        if (rootDisplacementSquared > teleportDistance * teleportDistance)
        {
            Wake(PhysicsChainWakeReason.RootTeleport);
            return false;
        }

        if (ComputeSleepConfiguredRootSignature() != _sleepConfiguredRootSignature)
        {
            Wake(PhysicsChainWakeReason.RootMovement);
            return false;
        }

        if (ComputeSleepColliderShapeSignature() != _sleepColliderShapeSignature)
        {
            Wake(PhysicsChainWakeReason.ColliderShapeChanged);
            return false;
        }

        if (ComputeSleepColliderPoseSignature() != _sleepColliderPoseSignature)
        {
            Wake(PhysicsChainWakeReason.ColliderPoseChanged);
            return false;
        }

        PhysicsChainActivityThresholds thresholds = GetActivityThresholds();
        var signals = new PhysicsChainActivitySignals(
            _lastActivitySnapshot.MaximumParticleDisplacementSquared,
            _lastActivitySnapshot.MaximumConstraintErrorSquared,
            rootAccelerationSquared,
            externalForceSquared,
            ColliderChanged: false,
            RecentlyVisibleOrUsed: _recentUseFramesRemaining > 0);
        float normalizedErrorSquared = PhysicsChainActivityEvaluation.ComputeNormalizedErrorSquared(signals, thresholds);
        _lastActivitySnapshot = new PhysicsChainActivitySnapshot(
            signals.MaximumParticleVelocitySquared,
            signals.MaximumConstraintErrorSquared,
            rootDisplacementSquared,
            rootAccelerationSquared,
            externalForceSquared,
            normalizedErrorSquared,
            _quietSimulationFrames,
            IsQuiet: PhysicsChainActivityEvaluation.IsQuiet(normalizedErrorSquared),
            ColliderChanged: false,
            RecentlyVisibleOrUsed: signals.RecentlyVisibleOrUsed);
        if (!PhysicsChainActivityEvaluation.ShouldWake(normalizedErrorSquared, thresholds.WakeMultiplier))
            return true;

        PhysicsChainWakeReason reason = rootAccelerationSquared > 0.0f
            ? PhysicsChainWakeReason.RootAcceleration
            : externalForceSquared > 0.0f
                ? PhysicsChainWakeReason.ExternalForceChanged
                : PhysicsChainWakeReason.AccumulatedError;
        Wake(reason);
        return false;
    }

    private PhysicsChainActivityThresholds GetActivityThresholds()
        => new(
            _sleepVelocityThreshold,
            _sleepConstraintErrorThreshold,
            _sleepRootAccelerationThreshold,
            _sleepExternalForceThreshold,
            _sleepWakeThresholdMultiplier);

    private void HoldSleepingOutputHistory()
    {
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
                particles[particleIndex].PreviousPhysicsPosition = particles[particleIndex].Position;
        }
        HoldCpuOutputHistory();
        _cpuBackendWorld?.HoldOutputHistory(_runtimeHandle);
    }

    private void EvaluateRuntimeSleep()
    {
        if (UseGPU || !EnableAutomaticSleep || _effectiveQualityTier == PhysicsChainQualityTier.Sleep)
            return;

        float rootDisplacementSquared = _objectMove.LengthSquared();
        Vector3 rootAcceleration = _objectMove - _previousActivityRootMove;
        _previousActivityRootMove = _objectMove;
        float rootAccelerationSquared = rootAcceleration.LengthSquared();
        float externalForceSquared = Force.LengthSquared();
        float maximumParticleDisplacementSquared = 0.0f;
        float maximumConstraintErrorSquared = 0.0f;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 1; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                float displacementSquared = Vector3.DistanceSquared(particle.Position, particle.PrevPosition);
                maximumParticleDisplacementSquared = MathF.Max(maximumParticleDisplacementSquared, displacementSquared);
                if ((uint)particle.ParentIndex >= (uint)particles.Count)
                    continue;
                float segmentLength = Vector3.Distance(particle.Position, particles[particle.ParentIndex].Position);
                float constraintError = MathF.Abs(segmentLength - particle.SegmentLength);
                maximumConstraintErrorSquared = MathF.Max(maximumConstraintErrorSquared, constraintError * constraintError);
            }
        }

        int colliderShapeSignature = ComputeSleepColliderShapeSignature();
        int colliderPoseSignature = ComputeSleepColliderPoseSignature();
        bool colliderChanged = (_activityColliderShapeSignature != 0 && colliderShapeSignature != _activityColliderShapeSignature)
            || (_activityColliderPoseSignature != 0 && colliderPoseSignature != _activityColliderPoseSignature);
        _activityColliderShapeSignature = colliderShapeSignature;
        _activityColliderPoseSignature = colliderPoseSignature;
        bool recentlyVisibleOrUsed = _recentUseFramesRemaining > 0;
        if (recentlyVisibleOrUsed)
            --_recentUseFramesRemaining;

        PhysicsChainActivityThresholds thresholds = GetActivityThresholds();
        var signals = new PhysicsChainActivitySignals(
            maximumParticleDisplacementSquared,
            maximumConstraintErrorSquared,
            rootAccelerationSquared,
            externalForceSquared,
            colliderChanged,
            recentlyVisibleOrUsed);
        float normalizedErrorSquared = PhysicsChainActivityEvaluation.ComputeNormalizedErrorSquared(signals, thresholds);
        bool quiet = PhysicsChainActivityEvaluation.IsQuiet(normalizedErrorSquared);
        int nextQuietFrameCount = quiet ? _quietSimulationFrames + 1 : 0;
        _lastActivitySnapshot = new PhysicsChainActivitySnapshot(
            maximumParticleDisplacementSquared,
            maximumConstraintErrorSquared,
            rootDisplacementSquared,
            rootAccelerationSquared,
            externalForceSquared,
            normalizedErrorSquared,
            nextQuietFrameCount,
            quiet,
            colliderChanged,
            recentlyVisibleOrUsed);

        if (!quiet)
        {
            Wake(colliderChanged
                ? PhysicsChainWakeReason.ColliderMovement
                : recentlyVisibleOrUsed
                    ? PhysicsChainWakeReason.VisibilityOrUse
                    : PhysicsChainWakeReason.ActivityExceeded);
            return;
        }

        _quietSimulationFrames = nextQuietFrameCount;
        if (!ShouldEnterAutomaticSleep(_quietSimulationFrames, _sleepQuietFrameCount))
            return;

        _isRuntimeSleeping = true;
        _sleepRootPosition = Transform.WorldTranslation;
        _sleepRootStep = Vector3.Zero;
        _sleepLastRootPosition = _sleepRootPosition;
        _sleepColliderSignature = ComputeSleepColliderSignature();
        _sleepColliderShapeSignature = ComputeSleepColliderShapeSignature();
        _sleepColliderPoseSignature = ComputeSleepColliderPoseSignature();
        _sleepConfiguredRootSignature = ComputeSleepConfiguredRootSignature();
        HoldSleepingOutputHistory();
    }

    internal static bool ShouldRetainSleep(float normalizedErrorSquared, float wakeMultiplier)
        => !PhysicsChainActivityEvaluation.ShouldWake(normalizedErrorSquared, wakeMultiplier);

    internal static bool ShouldEnterAutomaticSleep(int quietSimulationFrames, int requiredQuietSimulationFrames)
        => quietSimulationFrames >= Math.Max(requiredQuietSimulationFrames, 1);

    private int ComputeSleepConfiguredRootSignature()
    {
        HashCode hash = new();
        hash.Add(Root is null ? 0 : RuntimeHelpers.GetHashCode(Root));
        if (Root is not null)
            hash.Add(Root.WorldMatrix);

        int rootCount = Roots?.Count ?? 0;
        hash.Add(rootCount);
        for (int i = 0; i < rootCount; ++i)
        {
            Transform? root = Roots![i];
            if (root is null)
            {
                hash.Add(0);
                continue;
            }

            hash.Add(RuntimeHelpers.GetHashCode(root));
            hash.Add(root.WorldMatrix);
        }

        hash.Add(RootBone is null ? 0 : RuntimeHelpers.GetHashCode(RootBone));
        if (RootBone is not null)
            hash.Add(RootBone.WorldMatrix);

        return hash.ToHashCode();
    }

    private int ComputeSleepColliderSignature()
    {
        HashCode hash = new();
        hash.Add(ComputeSleepColliderShapeSignature());
        hash.Add(ComputeSleepColliderPoseSignature());
        return hash.ToHashCode();
    }

    private int ComputeSleepColliderShapeSignature()
    {
        HashCode hash = new();
        List<PhysicsChainColliderBase>? colliders = Colliders;
        int colliderCount = colliders?.Count ?? 0;
        hash.Add(colliderCount);

        for (int i = 0; i < colliderCount; ++i)
        {
            PhysicsChainColliderBase? collider = colliders![i];
            if (collider is null)
            {
                hash.Add(0);
                continue;
            }

            hash.Add(RuntimeHelpers.GetHashCode(collider));
            hash.Add(collider.IsActiveInHierarchy);
            hash.Add(collider._direction);
            hash.Add(collider._center);
            hash.Add(collider._bound);

            TransformBase? colliderTransform = collider switch
            {
                PhysicsChainSphereCollider sphere when sphere.ColliderTransform is not null => sphere.ColliderTransform,
                PhysicsChainCapsuleCollider capsule when capsule.ColliderTransform is not null => capsule.ColliderTransform,
                PhysicsChainBoxCollider box when box.ColliderTransform is not null => box.ColliderTransform,
                _ => collider.Transform,
            };
            hash.Add(colliderTransform is null ? 0 : RuntimeHelpers.GetHashCode(colliderTransform));

            switch (collider)
            {
                case PhysicsChainSphereCollider sphere:
                    hash.Add(sphere.Radius);
                    break;
                case PhysicsChainCapsuleCollider capsule:
                    hash.Add(capsule.Radius);
                    hash.Add(capsule.Height);
                    break;
                case PhysicsChainBoxCollider box:
                    hash.Add(box.Size);
                    break;
                case PhysicsChainCollider legacy:
                    hash.Add(legacy._radius);
                    hash.Add(legacy._height);
                    hash.Add(legacy._radius2);
                    break;
            }
        }

        return hash.ToHashCode();
    }

    private int ComputeSleepColliderPoseSignature()
    {
        HashCode hash = new();
        List<PhysicsChainColliderBase>? colliders = Colliders;
        int colliderCount = colliders?.Count ?? 0;
        hash.Add(colliderCount);
        for (int i = 0; i < colliderCount; ++i)
        {
            PhysicsChainColliderBase? collider = colliders![i];
            if (collider is null)
            {
                hash.Add(0);
                continue;
            }
            TransformBase? colliderTransform = collider switch
            {
                PhysicsChainSphereCollider sphere when sphere.ColliderTransform is not null => sphere.ColliderTransform,
                PhysicsChainCapsuleCollider capsule when capsule.ColliderTransform is not null => capsule.ColliderTransform,
                PhysicsChainBoxCollider box when box.ColliderTransform is not null => box.ColliderTransform,
                _ => collider.Transform,
            };
            if (colliderTransform is not null)
                hash.Add(colliderTransform.WorldMatrix);
            if (collider is PhysicsChainPlaneCollider plane)
                hash.Add(plane._plane);
        }
        return hash.ToHashCode();
    }

    private void CheckDistance()
    {
        if (!DistantDisable)
            return;

        TransformBase? rt = ReferenceObject;
        if (rt is null)
            return;

        rt.RecalculateMatrices();
        float d2 = (rt.WorldTranslation - Transform.WorldTranslation).LengthSquared();
        bool disable = d2 > DistanceToObject * DistanceToObject;
        if (disable == _distantDisabled)
            return;
        
        if (!disable)
            ResetParticlesPosition();
        _distantDisabled = disable;
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (!_isValidating)
        {
            PhysicsChainWakeReason wakeReason = GetAuthoredWakeReason(propName);
            if (wakeReason != PhysicsChainWakeReason.None)
                Wake(wakeReason);
        }

        if (IsIndependentQualityPolicyProperty(propName))
            ApplyCpuQualityPolicy();
        if (propName is nameof(RuntimeVisible) or nameof(RuntimeHandle))
            return;
        if (propName == nameof(EnableRigidGpuRestInputCache))
        {
            _cpuBackendWorld?.InvalidateRigidGpuRestInputCache(_runtimeHandle, this);
            return;
        }
        if (propName == nameof(UseGpuDrivenSkinning))
        {
            InvalidateGpuDrivenRenderers();
            return;
        }
        if (HandleGpuExecutionModePropertyChanged(propName, prev, field))
            return;

        // Skip validation during active simulation to avoid triggering
        // SetupParticles which queues a rebuild every frame.
        if (!_isSimulating)
            OnValidate();
    }

    private static PhysicsChainWakeReason GetAuthoredWakeReason(string? propertyName)
        => propertyName switch
        {
            nameof(Force) => PhysicsChainWakeReason.ExternalForceChanged,
            nameof(Root) or nameof(Roots) or nameof(RootBone) => PhysicsChainWakeReason.RootConfigurationChanged,
            nameof(Colliders) => PhysicsChainWakeReason.ColliderConfigurationChanged,
            nameof(QualityTier) or nameof(SimulationPolicy)
                or nameof(CollisionPolicy)
                or nameof(PalettePolicy)
                or nameof(BoundsPolicy)
                or nameof(TransformMirrorPolicy)
                => PhysicsChainWakeReason.QualityPolicyChanged,
            nameof(EnableAutomaticSleep) or nameof(SleepVelocityThreshold)
                or nameof(SleepConstraintErrorThreshold) or nameof(SleepRootAccelerationThreshold)
                or nameof(SleepExternalForceThreshold) or nameof(SleepWakeThresholdMultiplier)
                or nameof(SleepTeleportDistance) or nameof(SleepRecentUseFrameCount)
                or nameof(SleepQuietFrameCount)
                => PhysicsChainWakeReason.SleepPolicyChanged,
            nameof(DistantDisable) or nameof(ReferenceObject) or nameof(DistanceToObject)
                or nameof(AutomaticQualityRelevance) or nameof(AutomaticQualityImportance)
                or nameof(OffscreenBehavior) or nameof(OffscreenDecayFrameCount)
                or nameof(RecentInteractionQualityFrameCount)
                => PhysicsChainWakeReason.RelevanceChanged,
            nameof(UpdateRate) or nameof(Speed) or nameof(UpdateMode)
                or nameof(Damping) or nameof(DampingDistrib)
                or nameof(Elasticity) or nameof(ElasticityDistrib)
                or nameof(Stiffness) or nameof(StiffnessDistrib)
                or nameof(Inert) or nameof(InertDistrib)
                or nameof(Friction) or nameof(FrictionDistrib)
                or nameof(Radius) or nameof(RadiusDistrib)
                or nameof(EndLength) or nameof(EndOffset) or nameof(EndOffsetInBoneSpace)
                or nameof(GravityFalloff) or nameof(InertFollowsRootParent)
                or nameof(RestFollowsParent) or nameof(PinBranchChildren)
                or nameof(StiffnessMode) or nameof(GravityMode)
                or nameof(CollisionMode) or nameof(CarryParticlesBetweenSteps)
                or nameof(LimitType) or nameof(MaxAngle) or nameof(MaxAngleDistrib)
                or nameof(MaxYaw) or nameof(MaxYawDistrib) or nameof(LimitRotation)
                or nameof(MaxStretch) or nameof(MaxSquish) or nameof(StretchMotion)
                or nameof(Gravity) or nameof(BlendWeight)
                or nameof(Exclusions) or nameof(FreezeAxis)
                or nameof(RootInertia) or nameof(VelocitySmoothing)
                => PhysicsChainWakeReason.AuthoredParameterChanged,
            _ => PhysicsChainWakeReason.None,
        };

    private void OnValidate()
    {
        if (_isValidating)
            return;

        _isValidating = true;
        try
        {
            UpdateRate = MathF.Max(UpdateRate, 0);
            Speed = MathF.Max(Speed, 0);
            Damping = Damping.Clamp(0, 1);
            Elasticity = Elasticity.Clamp(0, 1);
            Stiffness = Stiffness.Clamp(0, 1);
            Inert = Inert.Clamp(0, 1);
            Friction = Friction.Clamp(0, 1);
            Radius = MathF.Max(Radius, 0);

            if (_particleTrees.Count == 0)
                return;

            if (IsRootChanged())
            {
                InitTransforms();
                SetupParticles();
            }
            else
                UpdateParameters();
        }
        finally
        {
            _isValidating = false;
        }
    }

    /// <summary>
    /// Reads the rest pose of each particle from its initial local pose and the current parent
    /// world matrix. The root keeps its current local translation. No transform changes.
    /// </summary>
    private static void ReadRestPose(ParticleTree pt)
    {
        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle particle = particles[i];
            Transform? transform = particle.Transform;
            if (transform is null)
                continue;

            bool isRoot = particle.ParentIndex < 0 || particle.ParentIndex >= particleCount;
            TransformState state = transform.FrameState;
            Vector3 localTranslation = isRoot ? state.Translation : particle.InitLocalPosition;
            // Particles are in parent-first order, so the parent rest matrix is ready.
            Matrix4x4 parentWorld = !isRoot && particles[particle.ParentIndex].Transform is not null
                ? particles[particle.ParentIndex].TransformLocalToWorldMatrix
                : transform.Parent?.WorldMatrix ?? Matrix4x4.Identity;
            Matrix4x4 world = PhysicsChainRestTransformMatrix.Compose(
                localTranslation, particle.InitLocalRotation, state.Scale, state.Order).ToMatrix4x4() * parentWorld;

            particle.TransformPosition = world.Translation;
            particle.TransformLocalPosition = localTranslation;
            particle.TransformLocalToWorldMatrix = world;
            if (i == 0)
                pt.RestGravity = Vector3.TransformNormal(pt.LocalGravity, world);
        }
    }

    private List<Transform> CollectConfiguredRoots()
    {
        _configuredRootsScratch.Clear();
        _configuredRootSetScratch.Clear();

        if (Root is not null && _configuredRootSetScratch.Add(Root))
            _configuredRootsScratch.Add(Root);

        if (Roots is not null)
        {
            for (int i = 0; i < Roots.Count; ++i)
            {
                Transform? root = Roots[i];
                if (root is not null && _configuredRootSetScratch.Add(root))
                    _configuredRootsScratch.Add(root);
            }
        }

        if (_configuredRootsScratch.Count == 0)
        {
            Transform? fallbackRoot = DefaultTransform;
            if (fallbackRoot is not null)
            {
                _configuredRootSetScratch.Add(fallbackRoot);
                _configuredRootsScratch.Add(fallbackRoot);
            }
        }

        return _configuredRootsScratch;
    }

    private bool IsRootChanged()
    {
        List<Transform> roots = CollectConfiguredRoots();

        if (roots.Count != _particleTrees.Count)
            return true;

        for (int i = 0; i < roots.Count; ++i)
            if (roots[i] != _particleTrees[i].Root)
                return true;
        
        return false;
    }

    public void SetWeight(float w)
    {
        if (_weight == w)
            return;
        
        if (w == 0)
            InitTransforms();
        else if (_weight == 0)
            ResetParticlesPosition();

        _weight = BlendWeight = w;
    }

    public float Weight => _weight;

    internal static float ComputeSimulationTimeScale(float stepDelta, float referenceDelta, float speed)
    {
        float safeSpeed = MathF.Max(0.0f, speed);
        float safeReference = MathF.Max(referenceDelta, 1e-6f);
        float safeStep = MathF.Max(0.0f, stepDelta);
        return (safeStep / safeReference) * safeSpeed;
    }

    private float ResolveSimulationReferenceDelta()
    {
        float updateRate = ResolveEffectiveUpdateRate();
        if (updateRate > 0.0f)
            return 1.0f / updateRate;

        return MathF.Max(RuntimeTimingServices.Current.UpdateDeltaSeconds, 1e-6f);
    }

    private void ResolveSimulationLoopAndTimeScale(float dt, out int loop, out float timeVar)
    {
        loop = 1;
        float stepDelta = dt;
        PhysicsChainQualityPolicy qualityPolicy = EffectiveQualityPolicy;
        float updateRate = qualityPolicy.SimulationRateHz;
        if (!qualityPolicy.SimulationEnabled)
        {
            loop = 0;
            timeVar = 0.0f;
            return;
        }

        if ((UpdateMode != EUpdateMode.Default || _effectiveQualityTier != PhysicsChainQualityTier.Strict) && updateRate > 0.0f)
        {
            if (_cpuBackendWorld is null || !_cpuBackendWorld.TryResolveSimulationLoop(
                _runtimeHandle, this, dt, updateRate, qualityPolicy.MaximumCatchUpSteps,
                out loop, out stepDelta))
            {
                loop = 0;
                timeVar = 0.0f;
                return;
            }
        }

        timeVar = ComputeSimulationTimeScale(stepDelta, ResolveSimulationReferenceDelta(), Speed);
    }

    private float ResolveEffectiveUpdateRate()
        => EffectiveQualityPolicy.SimulationRateHz;

    private void UpdateParticles()
    {
        if (_particleTrees.Count <= 0)
        {
            _lastSimulationProducedResults = false;
            return;
        }

        float dt = _deltaTime;
        ResolveSimulationLoopAndTimeScale(dt, out int loop, out float timeVar);

        bool producedResults = loop > 0;
        _lastSimulationProducedResults = producedResults;
        
        if (producedResults)
        {
            bool capture = UsesInterpolatedPresentation();
            for (int i = 0; i < loop; ++i)
            {
                if (capture)
                    CapturePreviousPose();
                CalculateParticles(timeVar, i);
                ApplyParticleTransforms(timeVar);
            }
        }
        else
            SkipUpdateParticles();
    }

    public void SetupParticles()
    {
        if (_isSimulating)
        {
            QueueRebuild("SetupParticles");
            return;
        }

        InvalidateReadbackSource();
        _particleTrees.Clear();

        Transform? componentTransform = DefaultTransform;
        if (componentTransform is null)
            return;

        List<Transform> roots = CollectConfiguredRoots();
        for (int i = 0; i < roots.Count; ++i)
            AppendParticleTree(roots[i]);

        _objectScale = MathF.Abs(componentTransform.LossyWorldScale.X);
        _objectPrevPosition = componentTransform.WorldTranslation;
        _objectMove = Vector3.Zero;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];

            // Activation-time setup can run before the hierarchy has produced valid
            // world-space child transforms. Force a hierarchy recalc before we sample
            // WorldTranslation so the initial chain rest pose matches the authored pose.
            if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
                pt.Root.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
            else
                pt.Root.RecalculateMatrixHierarchy(
                    forceWorldRecalc: true,
                    setRenderMatrixNow: false,
                    childRecalcType: PhysicsChainWorld.RequiresSequentialHierarchyEvaluation ? ELoopType.Sequential : ELoopType.Parallel).Wait();
            AppendParticles(pt, pt.Root, -1, 0.0f);

            if (pt.Particles.Count == 0)
            {
                LogFault($"SetupParticles:ZeroParticles:{FormatRoot(pt.Root)}",
                    $"SetupParticles created zero particles for root {FormatRoot(pt.Root)}.");
            }
        }

        UpdateParameters();
        _particlesVersion++;
        unchecked { ++_particleStateVersion; }
        _cpuBackendWorld?.MarkGpuRestOwnershipDirty();
        MarkGpuBuffersDirty();
        InvalidateGpuDrivenRenderers();
    }

    private void AppendParticleTree(Transform root)
    {
        if (root is null)
            return;

        _particleTrees.Add(new ParticleTree(root));
    }

    private (Vector3 LocalPosition, Quaternion LocalRotation) GetInitialLocalState(Transform transform)
    {
        if (_initialLocalStates.TryGetValue(transform, out var state))
            return state;

        state = (transform.LocalTranslation, transform.LocalRotation);
        _initialLocalStates[transform] = state;

        return state;
    }

    private void AppendParticles(ParticleTree tree, Transform? tfm, int parentIndex, float boneLength)
    {
        var ptcl = new Particle(tfm, parentIndex);

        if (tfm != null)
        {
            var initialLocalState = GetInitialLocalState(tfm);
            ptcl.Position = ptcl.PrevPosition = tfm.WorldTranslation;
            ptcl.PreviousPhysicsPosition = ptcl.Position;
            ptcl.InitLocalPosition = initialLocalState.LocalPosition;
            ptcl.InitLocalRotation = initialLocalState.LocalRotation;
        }
        else //end bone
        {
            if (parentIndex < 0 || parentIndex >= tree.Particles.Count)
            {
                LogFault($"AppendParticles:BadParent:{FormatRoot(tree.Root)}:{parentIndex}",
                    $"AppendParticles invalid parent index {parentIndex} (count={tree.Particles.Count}, root={FormatRoot(tree.Root)}).");
                return;
            }

            TransformBase? parent = tree.Particles[parentIndex].Transform;
            if (parent != null)
            {
                if (EndLength > 0.0f)
                {
                    TransformBase? parentParentTfm = parent.Parent;
                    Vector3 endOffset = parentParentTfm != null
                        ? parent.InverseTransformPoint(parent.WorldTranslation * 2.0f - parentParentTfm.WorldTranslation) * EndLength
                        : new Vector3(EndLength, 0.0f, 0.0f);
                    ptcl.EndOffset = endOffset;
                }
                else if (EndOffsetInBoneSpace)
                    ptcl.EndOffset = EndOffset;
                else
                    ptcl.EndOffset = parent.InverseTransformPoint(Transform.TransformDirection(EndOffset) + parent.WorldTranslation);
                                
                ptcl.Position = ptcl.PrevPosition = parent.TransformPoint(ptcl.EndOffset);
                ptcl.PreviousPhysicsPosition = ptcl.Position;
            }
            ptcl.InitLocalPosition = Vector3.Zero;
            ptcl.InitLocalRotation = Quaternion.Identity;
        }

        if (parentIndex >= 0 && parentIndex < tree.Particles.Count && tree.Particles[parentIndex].Transform is not null)
        {
            var parentPtcl = tree.Particles[parentIndex];
            var parentTfm = parentPtcl.Transform!;
            var parentPtclPos = parentTfm.WorldTranslation;
            ptcl.SegmentLength = (parentPtclPos - ptcl.Position).Length();
            boneLength += ptcl.SegmentLength;
            ptcl.BoneLength = boneLength;
            tree.BoneTotalLength = MathF.Max(tree.BoneTotalLength, boneLength);
            tree.Particles[parentIndex].ChildCount += 1;
        }

        int index = tree.Particles.Count;
        tree.Particles.Add(ptcl);

        if (tfm != null)
        {
            for (int i = 0; i < tfm.Children.Count; ++i)
            {
                TransformBase child = tfm.Children[i];

                bool exclude = false;
                if (Exclusions != null)
                    exclude = Exclusions.Contains(child);
                
                if (!exclude)
                    AppendParticles(tree, child as Transform, index, boneLength);
                else if (EndLength > 0.0f || EndOffset != Vector3.Zero)
                    AppendParticles(tree, null, index, boneLength);
            }

            if (tfm.Children.Count == 0 && (EndLength > 0.0f || EndOffset != Vector3.Zero))
                AppendParticles(tree, null, index, boneLength);
        }
    }

    public void UpdateParameters()
    {
        SetWeight(BlendWeight);
        for (int i = 0; i < _particleTrees.Count; ++i)
            UpdateParameters(_particleTrees[i]);

        _particlesVersion++;
    }

    private void UpdateParameters(ParticleTree pt)
    {
        pt.LocalGravity = Vector3.TransformNormal(Gravity, pt.RootWorldToLocalMatrix);

        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            p.Damping = Damping;
            p.Elasticity = Elasticity;
            p.Stiffness = Stiffness;
            p.Inert = Inert;
            p.Friction = Friction;
            p.Radius = Radius;
            float maxAngle = MaxAngle;
            float maxYaw = MaxYaw;

            if (pt.BoneTotalLength > 0)
            {
                float a = p.BoneLength / pt.BoneTotalLength;
                if (DampingDistrib != null && DampingDistrib.Keyframes.Count > 0)
                    p.Damping *= DampingDistrib.Evaluate(a);
                if (ElasticityDistrib != null && ElasticityDistrib.Keyframes.Count > 0)
                    p.Elasticity *= ElasticityDistrib.Evaluate(a);
                if (_stiffnessDistrib != null && _stiffnessDistrib.Keyframes.Count > 0)
                    p.Stiffness *= _stiffnessDistrib.Evaluate(a);
                if (InertDistrib != null && InertDistrib.Keyframes.Count > 0)
                    p.Inert *= InertDistrib.Evaluate(a);
                if (FrictionDistrib != null && FrictionDistrib.Keyframes.Count > 0)
                    p.Friction *= FrictionDistrib.Evaluate(a);
                if (RadiusDistrib != null && RadiusDistrib.Keyframes.Count > 0)
                    p.Radius *= RadiusDistrib.Evaluate(a);
                if (MaxAngleDistrib != null && MaxAngleDistrib.Keyframes.Count > 0)
                    maxAngle *= MaxAngleDistrib.Evaluate(a);
                if (MaxYawDistrib != null && MaxYawDistrib.Keyframes.Count > 0)
                    maxYaw *= MaxYawDistrib.Evaluate(a);
            }

            p.MaxAngleRadians = float.DegreesToRadians(Math.Clamp(maxAngle, 0.0f, 180.0f));
            p.MaxYawRadians = float.DegreesToRadians(Math.Clamp(maxYaw, 0.0f, 180.0f));

            p.Damping = p.Damping.Clamp(0, 1);
            p.Elasticity = p.Elasticity.Clamp(0, 1);
            p.Stiffness = p.Stiffness.Clamp(0, 1);
            p.Inert = p.Inert.Clamp(0, 1);
            p.Friction = p.Friction.Clamp(0, 1);
            p.Radius = MathF.Max(p.Radius, 0);
        }
    }

    private void UpdateRootParentMotion(ParticleTree pt)
    {
        if (!InertFollowsRootParent)
            return;

        TransformBase? parent = pt.Root.Parent;
        if (parent is null)
        {
            pt.RootParentMotion = Matrix4x4.Identity;
            return;
        }

        Matrix4x4 current = parent.WorldMatrix;
        pt.RootParentMotion = pt.HasPreviousRootParentWorld && Matrix4x4.Invert(pt.PreviousRootParentWorld, out Matrix4x4 inverse)
            ? inverse * current
            : Matrix4x4.Identity;
        pt.LatestRootParentWorld = current;
        if (!pt.HasPreviousRootParentWorld)
        {
            pt.PreviousRootParentWorld = current;
            pt.HasPreviousRootParentWorld = true;
        }
    }

    private void InitTransforms()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            InitTransforms(_particleTrees[i]);
    }

    private static void InitTransforms(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
            return;

        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle p = particles[i];
            if (p.Transform is null)
                continue;

            if (p.ParentIndex < 0)
            {
                // Root: restore rotation only; translation is driven by animation/external input.
                p.Transform.Rotation = p.InitLocalRotation;
                continue;
            }

            p.Transform.SetLocalTranslationRotation(p.InitLocalPosition, p.InitLocalRotation);
        }
    }

    private void ResetParticlesPosition()
    {
        InvalidateReadbackSource();
        for (int i = 0; i < _particleTrees.Count; ++i)
            ResetParticlesPosition(_particleTrees[i]);

        unchecked
        {
            ++_particleStateVersion;
        }

        _objectPrevPosition = Transform.WorldTranslation;
        _cpuBackendWorld?.ResetSimulationClock(_runtimeHandle, this);
        _preUpdateCount = 0;
        InitializeRootBoneTracking();
    }

    private void InitializeRootBoneTracking()
    {
        if (_rootBone is not null)
        {
            _rootBone.RecalculateMatrices();
            _rootBonePrevPosition = _rootBone.WorldTranslation;
        }
        _smoothedObjectMove = Vector3.Zero;
        _hasPendingGpuBoneSync = false;
    }

    private static void ResetParticlesPosition(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"ResetParticles:NoParticles:{FormatRoot(pt.Root)}", $"ResetParticlesPosition skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle p = particles[i];
            if (p.Transform is not null)
                p.Position = p.PrevPosition = p.Transform.WorldTranslation;
            else // end bone
            {
                if (p.ParentIndex < 0 || p.ParentIndex >= particleCount)
                {
                    LogFault($"ResetParticles:BadParent:{FormatRoot(pt.Root)}:{p.ParentIndex}",
                        $"ResetParticlesPosition invalid parent index {p.ParentIndex} for particle {i} (count={particleCount}, root={FormatRoot(pt.Root)}).");
                    continue;
                }

                Transform? pb = particles[p.ParentIndex].Transform;
                if (pb is not null)
                    p.Position = p.PrevPosition = pb.TransformPoint(p.EndOffset);
            }
            p.PreviousPhysicsPosition = p.Position;
            p.IsColliding = false;
        }
    }

    private bool UsesInterpolatedPresentation()
        => InterpolationMode != EInterpolationMode.Discrete
            && (UpdateMode == EUpdateMode.FixedUpdate
                || _effectiveQualityTier != PhysicsChainQualityTier.Strict
                || (UpdateMode != EUpdateMode.Default && ResolveEffectiveUpdateRate() > 0.0f));

    private bool ShouldRefreshInterpolatedRenderPose()
        => UsesInterpolatedPresentation() && (!UseGPU || GpuSyncToBones);

    private float ComputeRenderAlpha()
    {
        PhysicsChainSimulationClock clock = _cpuBackendWorld is not null
            && _cpuBackendWorld.TryGetSimulationClock(_runtimeHandle, this, out PhysicsChainSimulationClock current)
                ? current
                : default;
        if (UpdateMode == EUpdateMode.FixedUpdate)
        {
            long fixedDeltaTicks = RuntimeTimingServices.Current.FixedDeltaTicks;
            long intervalTicks = fixedDeltaTicks;
            float fixedUpdateRate = ResolveEffectiveUpdateRate();
            if (fixedUpdateRate > 0.0f)
                intervalTicks = Math.Max(intervalTicks, (long)(TimeSpan.TicksPerSecond / fixedUpdateRate));
            return intervalTicks <= 0L
                ? 1.0f
                : Math.Clamp((float)(Math.Max(0L, clock.FixedRenderAccumulatedTicks) / (double)intervalTicks), 0.0f, 1.0f);
        }

        float updateRate = ResolveEffectiveUpdateRate();
        if (updateRate > 0.0f)
        {
            float frameTime = 1.0f / updateRate;
            return Math.Clamp(clock.ElapsedSeconds / frameTime, 0.0f, 1.0f);
        }

        return 1.0f;
    }

    private void CapturePreviousPose()
    {
        var trees = _particleTreesForJob;
        int treeCount = _particleTreesForJobCount;
        for (int treeIndex = 0; treeIndex < treeCount; ++treeIndex)
        {
            List<Particle> particles = trees![treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
                particles[particleIndex].PreviousPhysicsPosition = particles[particleIndex].Position;
        }
    }

    internal static float ComputeFixedUpdateRenderAlpha(long accumulatedTimeTicks, long fixedDeltaTicks)
        => fixedDeltaTicks <= 0L
            ? 0.0f
            : Math.Clamp((float)(Math.Max(0L, accumulatedTimeTicks) / (double)fixedDeltaTicks), 0.0f, 1.0f);

    internal static bool ShouldUseImmediateFixedUpdatePose(EInterpolationMode mode, long updateDeltaTicks, long fixedDeltaTicks)
        => mode == EInterpolationMode.Discrete || updateDeltaTicks > fixedDeltaTicks;

    internal static Vector3 ResolveFixedUpdateRenderPosition(Vector3 previousPosition, Vector3 currentPosition, EInterpolationMode mode, float alpha)
        => mode switch
        {
            EInterpolationMode.Interpolate => Vector3.Lerp(previousPosition, currentPosition, alpha),
            EInterpolationMode.Extrapolate => currentPosition + ((currentPosition - previousPosition) * alpha),
            _ => currentPosition,
        };

    private void ApplyCurrentParticleTransforms(bool newSimulationResults = false)
    {
        if (UsesInterpolatedPresentation())
        {
            if (newSimulationResults && UpdateMode == EUpdateMode.FixedUpdate)
                _cpuBackendWorld?.ClearFixedRenderClock(_runtimeHandle, this);

            float alpha = ComputeRenderAlpha();
            ApplyParticlesToTransforms(InterpolationMode, alpha);
            return;
        }

        ApplyParticlesToTransforms();
    }

    private void ApplyInterpolatedRenderPose()
    {
        if (!ShouldRefreshInterpolatedRenderPose())
            return;

        float alpha = ComputeRenderAlpha();
        ApplyParticlesToTransforms(InterpolationMode, alpha);
    }

    private void CalculateParticles(float timeVar, int loopIndex)
    {
        var trees = _particleTreesForJob;
        int treeCount = _particleTreesForJobCount;
        for (int i = 0; i < treeCount; ++i)
            CalculateParticles(trees![i], timeVar, loopIndex);
    }

    private void CalculateParticles(ParticleTree pt, float timeVar, int loopIndex)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"CalculateParticles:NoParticles:{FormatRoot(pt.Root)}", $"CalculateParticles skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        // Segment ratio gravity is applied per segment after the pull.
        Vector3 force = GravityMode == EPhysicsChainGravityMode.Force ? Gravity : Vector3.Zero;
        float gravityLengthSquared = force.LengthSquared();
        if (gravityLengthSquared > 1e-8f)
        {
            Vector3 fdir = Gravity / MathF.Sqrt(gravityLengthSquared);
            Vector3 pf = fdir * MathF.Max(Vector3.Dot(pt.RestGravity, fdir), 0.0f); // project current gravity to rest gravity
            force -= pf * GravityFalloff; // remove projected gravity
        }
        force = (force + Force) * (_objectScale * timeVar);

        Vector3 objectMove = loopIndex == 0 ? _objectMove : Vector3.Zero; // only first loop consider object move
        bool followRootParent = InertFollowsRootParent && loopIndex == 0;
        pt.PresentationOffset = Vector3.Zero;
        Matrix4x4 rootParentMotion = pt.RootParentMotion;

        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle p = particles[i];
            if (p.ParentIndex >= 0)
            {
                // verlet integration
                Vector3 v = p.Position - p.PrevPosition;
                Vector3 rmove = followRootParent
                    ? (Vector3.Transform(p.Position, rootParentMotion) - p.Position) * p.Inert
                    : InertFollowsRootParent ? Vector3.Zero : objectMove * p.Inert;
                p.PrevPosition = p.Position + rmove;
                float damping = p.Damping;
                if (p.IsColliding)
                {
                    damping += p.Friction;
                    if (damping > 1)
                        damping = 1;
                    p.IsColliding = false;
                }
                p.Position += v * (1.0f - damping) + force + rmove;
            }
            else
            {
                p.PrevPosition = p.Position;
                p.Position = p.TransformPosition;
            }
        }

        // The motion of frames without a step accumulates until a step applies it.
        if (followRootParent)
            pt.PreviousRootParentWorld = pt.LatestRootParentWorld;
    }

    private void ApplyParticleTransforms(float timeVar)
    {
        var trees = _particleTreesForJob;
        int treeCount = _particleTreesForJobCount;
        for (int i = 0; i < treeCount; ++i)
            ApplyParticleTransforms(trees![i], timeVar);
    }

    private void ApplyParticleTransforms(ParticleTree pt, float timeVar)
    {
        if (pt.Particles is null || pt.Particles.Count <= 1)
        {
            LogFault($"ApplyParticleTransforms:NoParticles:{FormatRoot(pt.Root)}", $"ApplyParticleTransforms skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        bool collisionEnabled = EffectiveQualityPolicy.CollisionEnabled;
        for (int i = 1; i < particleCount; ++i)
        {
            Particle childPtcl = particles[i];
            if (childPtcl.ParentIndex < 0 || childPtcl.ParentIndex >= particleCount)
            {
                LogFault($"ApplyParticleTransforms:BadParent:{FormatRoot(pt.Root)}:{childPtcl.ParentIndex}",
                    $"ApplyParticleTransforms invalid parent index {childPtcl.ParentIndex} for particle {i} (count={particleCount}, root={FormatRoot(pt.Root)}).");
                continue;
            }

            Particle parentPtcl = particles[childPtcl.ParentIndex];

            if (TryPinBranchChild(particles, childPtcl, parentPtcl))
                continue;

            float restLen = childPtcl.SegmentLength;

            // keep shape
            float stiffness = Interp.Lerp(1.0f, childPtcl.Stiffness, _weight);
            ApplyKeepShape(pt, childPtcl, parentPtcl, stiffness, restLen, timeVar);

            // collide
            PhysicsChainColliderSnapshot[]? colliderSnapshots = _colliderSnapshotsForJob;
            int colliderSnapshotCount = _colliderSnapshotsForJobCount;
            if (collisionEnabled && colliderSnapshots is not null && colliderSnapshotCount > 0)
            {
                for (int j = 0; j < colliderSnapshotCount; ++j)
                    childPtcl.IsColliding |= CollideSnapshot(in colliderSnapshots[j], childPtcl, parentPtcl);
            }

            PhysicsChainColliderBase[]? fallbackColliders = _fallbackCollidersForJob;
            int fallbackColliderCount = _fallbackCollidersForJobCount;
            if (collisionEnabled && fallbackColliders is not null && fallbackColliderCount > 0)
            {
                for (int j = 0; j < fallbackColliderCount; ++j)
                    childPtcl.IsColliding |= CollideComponent(fallbackColliders[j], childPtcl, parentPtcl);
            }

            // freeze axis, project to plane 
            if (FreezeAxis != EFreezeAxis.None)
            {
                Matrix4x4 matrix = parentPtcl.TransformLocalToWorldMatrix;
                Vector4 planeNormal = FreezeAxis switch
                {
                    EFreezeAxis.X => new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41),
                    EFreezeAxis.Y => new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42),
                    _ => new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43),
                };
                planeNormal = planeNormal.Normalized();
                Plane movePlane = XRMath.CreatePlaneFromPointAndNormal(parentPtcl.Position, planeNormal.XYZ());
                childPtcl.Position -= movePlane.Normal * GeoUtil.DistanceFrom.PlaneToPoint(movePlane, childPtcl.Position);
            }

            if (LimitType != EPhysicsChainLimitType.None)
            {
                Matrix4x4 limitParent = parentPtcl.TransformLocalToWorldMatrix;
                limitParent.Translation = parentPtcl.Position;
                Vector3 limitRest = childPtcl.Transform is not null
                    ? Vector3.Transform(childPtcl.TransformLocalPosition, limitParent)
                    : Vector3.Transform(childPtcl.EndOffset, limitParent);
                ApplyAngleLimit(particles, childPtcl, parentPtcl, limitRest);
            }

            // keep length
            ApplySegmentLength(childPtcl, parentPtcl, restLen);

            if (TracksSegmentTurn)
                UpdateSegmentTurn(childPtcl, parentPtcl);
        }
    }

    private void SkipUpdateParticles()
    {
        var trees = _particleTreesForJob;
        int treeCount = _particleTreesForJobCount;
        for (int i = 0; i < treeCount; ++i)
            SkipUpdateParticles(trees![i]);
    }

    //Only update stiffness and keep bone length
    private void SkipUpdateParticles(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"SkipUpdateParticles:NoParticles:{FormatRoot(pt.Root)}", $"SkipUpdateParticles skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;

        // Particles keep their state until the next step. The presented pose moves rigidly with the root.
        if (!CarryParticlesBetweenSteps)
        {
            Particle root = particles[0];
            pt.PresentationOffset = root.TransformPosition - root.Position;
            return;
        }

        for (int i = 0; i < particleCount; ++i)
        {
            Particle childPtcl = particles[i];
            if (childPtcl.ParentIndex >= 0)
            {
                childPtcl.PrevPosition += _objectMove;
                childPtcl.Position += _objectMove;

                if (childPtcl.ParentIndex >= particleCount)
                {
                    LogFault($"SkipUpdateParticles:BadParent:{FormatRoot(pt.Root)}:{childPtcl.ParentIndex}",
                        $"SkipUpdateParticles invalid parent index {childPtcl.ParentIndex} for particle {i} (count={particleCount}, root={FormatRoot(pt.Root)}).");
                    continue;
                }

                Particle parentPtcl = particles[childPtcl.ParentIndex];

                if (TryPinBranchChild(particles, childPtcl, parentPtcl))
                    continue;

                float restLen = childPtcl.SegmentLength;

                //Keep shape
                float stiffness = Interp.Lerp(1.0f, childPtcl.Stiffness, _weight);
                if (stiffness > 0 && StiffnessMode == EPhysicsChainStiffnessMode.DistanceLimit)
                    ApplyStiffnessDistanceLimit(childPtcl, parentPtcl.Position + ComputeShapeRestOffset(childPtcl, parentPtcl), stiffness, restLen);

                // keep length
                Vector3 dd = parentPtcl.Position - childPtcl.Position;
                float leng = dd.Length();
                if (leng > 0)
                    childPtcl.Position += dd * ((leng - restLen) / leng);

                if (TracksSegmentTurn)
                    UpdateSegmentTurn(childPtcl, parentPtcl);
            }
            else
            {
                childPtcl.PrevPosition = childPtcl.Position;
                childPtcl.Position = childPtcl.TransformPosition;
            }
        }
    }

    //static Vector3 MirrorVector(Vector3 v, Vector3 axis)
    //    => v - axis * (Vector3.Dot(v, axis) * 2);

    private void ApplyParticlesToTransforms()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i], EInterpolationMode.Discrete, 1.0f, PinBranchChildren);
    }

    private void ApplyParticlesToTransforms(EInterpolationMode mode, float alpha)
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i], mode, alpha, PinBranchChildren);
    }

    private const float MinimumSegmentDirectionLengthSquared = 1e-10f;

    private static void ApplyParticlesToTransforms(ParticleTree pt, EInterpolationMode mode, float alpha, bool pinBranchChildren)
    {
        if (pt.Particles is null || pt.Particles.Count <= 1)
        {
            LogFault($"ApplyParticlesToTransforms:NoParticles:{FormatRoot(pt.Root)}", $"ApplyParticlesToTransforms skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        // Each bone receives only its final local value, computed from its rest pose. A
        // render-matrix publish on the collect thread can run at any time during this loop,
        // so the loop never writes the rest pose itself to a bone.
        var particles = pt.Particles;
        using TransformHierarchyMutationBatch hierarchyBatch = pt.Root.BeginHierarchyMutationBatch();
        int particleCount = particles.Count;
        // The loop below does not aim a bone without children, so it keeps its rest rotation.
        for (int i = 0; i < particleCount; ++i)
            if (particles[i] is { ChildCount: 0, Transform: { } leafTfm } leaf)
                hierarchyBatch.SetLocalRotation(leafTfm, leaf.InitLocalRotation);

        for (int i = 1; i < particleCount; ++i)
        {
            Particle child = particles[i];
            if (child.ParentIndex < 0 || child.ParentIndex >= particleCount)
            {
                LogFault($"ApplyParticlesToTransforms:BadParent:{FormatRoot(pt.Root)}:{child.ParentIndex}",
                    $"ApplyParticlesToTransforms invalid parent index {child.ParentIndex} for particle {i} (count={particleCount}, root={FormatRoot(pt.Root)}).");
                continue;
            }

            Particle parent = particles[child.ParentIndex];

            Transform? pTfm = parent.Transform;
            Transform? cTfm = child.Transform;
            Vector3 parentPosition = ResolveFixedUpdateRenderPosition(parent.PreviousPhysicsPosition, parent.Position, mode, alpha) + pt.PresentationOffset;
            Vector3 childPosition = ResolveFixedUpdateRenderPosition(child.PreviousPhysicsPosition, child.Position, mode, alpha) + pt.PresentationOffset;

            if (pTfm is not null)
            {
                // A bone with more than one child keeps its rest rotation.
                Quaternion localRotation = parent.ChildCount <= 1
                    ? AimRestRotation(pTfm, parent.InitLocalRotation,
                        cTfm is not null ? child.InitLocalPosition : child.EndOffset,
                        childPosition - parentPosition)
                    : parent.InitLocalRotation;
                hierarchyBatch.SetLocalRotation(pTfm, localRotation);

                // The parent's own parent can have rotated earlier in this loop. The parent's
                // world and inverse-world matrices must include that rotation and this one
                // before the child's SetWorldTranslation, which uses ParentInverseWorldMatrix.
                hierarchyBatch.RecalculateMatrices(pTfm, forceWorldRecalc: true);
            }

            if (cTfm is null)
                continue;
            if (pinBranchChildren && parent.ChildCount > 1)
                hierarchyBatch.SetLocalTranslation(cTfm, child.InitLocalPosition);
            else
                hierarchyBatch.SetWorldTranslation(cTfm, childPosition);
        }
    }

    /// <summary>
    /// Returns the local rotation that turns a bone from its rest rotation so that its rest
    /// segment direction points along <paramref name="segment"/>. The parent world matrix of
    /// <paramref name="bone"/> must be current, and the local translation of the bone must be final.
    /// </summary>
    private static Quaternion AimRestRotation(Transform bone, Quaternion restLocalRotation,
        Vector3 restSegmentLocal, Vector3 segment)
    {
        TransformState state = bone.FrameState;
        Matrix4x4 restWorld = PhysicsChainRestTransformMatrix.Compose(
            state.Translation, restLocalRotation, state.Scale, state.Order).ToMatrix4x4()
            * (bone.Parent?.WorldMatrix ?? Matrix4x4.Identity);
        Vector3 restSegment = Vector3.TransformNormal(restSegmentLocal, restWorld);

        // A zero-length segment has no direction, so it cannot define a rotation.
        if (restSegment.LengthSquared() <= MinimumSegmentDirectionLengthSquared ||
            segment.LengthSquared() <= MinimumSegmentDirectionLengthSquared)
            return restLocalRotation;

        // The same world-to-local conversion as Transform.AddWorldRotationDelta, applied to the rest rotation.
        Quaternion worldDelta = Quaternion.Normalize(XRMath.RotationBetweenVectors(restSegment, segment));
        Quaternion parentWorldRotation = bone.ParentWorldRotation;
        Quaternion localDelta = Quaternion.Normalize(
            Quaternion.Inverse(parentWorldRotation) * worldDelta * parentWorldRotation);
        return Quaternion.Normalize(localDelta * restLocalRotation);
    }

}
