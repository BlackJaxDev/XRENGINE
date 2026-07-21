using XREngine.Extensions;
using JoltPhysicsSharp;
using System;
using System.Diagnostics;
using System.Numerics;
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Scene.Transforms;
using static XREngine.Engine;

namespace XREngine.Components;

public partial class PhysicsChainComponent : XRComponent, IRenderable
{
    private static readonly TimeSpan FaultLogInterval = TimeSpan.FromSeconds(2);

    private bool _isSimulating;
    private bool _isValidating;
    private bool _rebuildQueued;
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
        base.OnComponentActivated();
        ActivateGpuExecutionMode();
        SetupParticles();
        PhysicsChainWorld.Register(this);
        ResetParticlesPosition();
        InitializeRootBoneTracking();
        OnValidate();
    }
    protected override void OnComponentDeactivated()
    {
        PhysicsChainWorld.Unregister(this);
        DeactivateGpuExecutionMode();
        base.OnComponentDeactivated();
        InitTransforms();
        _preUpdateCount = 0;
        _time = 0.0f;
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
            _fixedUpdateRenderAccumulatedTicks += Math.Max(0L, Engine.Time.Timer.Update.DeltaTicks);
        
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

    internal void SetRuntimeHandle(PhysicsChainRuntimeHandle handle)
        => _runtimeHandle = handle;

    internal void SetEffectiveQualityTier(PhysicsChainQualityTier tier)
    {
        if (_effectiveQualityTier == tier)
            return;

        _effectiveQualityTier = tier;
        float rate = ResolveEffectiveUpdateRate();
        if (rate > 0.0f && _runtimeHandle.IsValid)
        {
            uint phaseHash = unchecked((uint)_runtimeHandle.Slot * 2654435761u);
            float phase = (phaseHash & 1023u) / 1024.0f;
            _time = phase / rate;
        }

        if (tier != PhysicsChainQualityTier.Sleep)
            Wake();
    }

    /// <summary>
    /// Wakes an automatically sleeping chain after gameplay, root, collider,
    /// or authoring input changes.
    /// </summary>
    public void Wake()
    {
        _isRuntimeSleeping = false;
        _quietSimulationFrames = 0;
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

    internal void SolveWorldLateTick()
        => UpdateParticles();

    internal void PublishWorldLateTick()
    {
        EvaluateRuntimeSleep();
        ApplyCurrentParticleTransforms(newSimulationResults: _lastSimulationProducedResults);
        CompleteWorldLateTick();
    }

    internal void AbortWorldLateTick()
    {
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

    private void Prepare()
    {
        using var profilerState = Profiler.Start("PhysicsChainComponent.Prepare");

        _deltaTime = Delta;
        switch (UpdateMode)
        {
            case EUpdateMode.Undilated:
                _deltaTime = UndilatedDelta;
                break;
            case EUpdateMode.FixedUpdate:
                _deltaTime = FixedDelta * _preUpdateCount;
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

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            if (pt.Particles is not { Count: > 0 })
                continue;

            // Restore rest-pose rotations before reading matrices. ApplyParticlesToTransforms()
            // writes simulation rotation deltas back to the hierarchy each frame; without this
            // reset the matrices we read below would include the previous frame's deformation,
            // causing the rest reference to drift.
            InitTransforms(pt);

            // Ensure we sample the current (post-InitTransforms / post-animation) pose.
            // Without this, we can end up using stale world matrices from the prior frame,
            // effectively allowing the simulated pose to slowly become the new "rest".
            using (Profiler.Start("PhysicsChainComponent.Prepare.HierarchyRecalc"))
            {
                long hierarchyStart = Stopwatch.GetTimestamp();
                RefreshPreparedParticleTree(pt);
                GPUPhysicsChainDispatcher.RecordHierarchyRecalcTicks(Stopwatch.GetTimestamp() - hierarchyStart);
            }

            pt.RestGravity = pt.Root.TransformDirection(pt.LocalGravity);

            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                if (p.Transform is not null)
                {
                    p.TransformPosition = p.Transform.WorldTranslation;
                    p.TransformLocalPosition = p.ParentIndex < 0 ? p.Transform.LocalTranslation : p.InitLocalPosition;
                    p.TransformLocalToWorldMatrix = p.Transform.WorldMatrix;
                }
            }
        }

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
        float wakeDistance = MathF.Max(_sleepVelocityThreshold * 4.0f, 1e-5f);
        if (Vector3.DistanceSquared(rootPosition, _sleepRootPosition) > wakeDistance * wakeDistance
            || Force.LengthSquared() > _sleepVelocityThreshold * _sleepVelocityThreshold)
        {
            Wake();
            return false;
        }

        return true;
    }

    private void EvaluateRuntimeSleep()
    {
        if (UseGPU || !EnableAutomaticSleep || _effectiveQualityTier == PhysicsChainQualityTier.Sleep)
            return;

        float thresholdSquared = _sleepVelocityThreshold * _sleepVelocityThreshold;
        bool quiet = _objectMove.LengthSquared() <= thresholdSquared;
        for (int treeIndex = 0; quiet && treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 1; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                if (Vector3.DistanceSquared(particle.Position, particle.PrevPosition) > thresholdSquared)
                {
                    quiet = false;
                    break;
                }
            }
        }

        if (!quiet)
        {
            Wake();
            return;
        }

        if (++_quietSimulationFrames < _sleepQuietFrameCount)
            return;

        _isRuntimeSleeping = true;
        _sleepRootPosition = Transform.WorldTranslation;
    }

    private void CheckDistance()
    {
        if (!DistantDisable)
            return;

        TransformBase? rt = ReferenceObject;
        if (rt is null)
        {
            XRCamera? c = ((State.MainPlayer?.ControlledPawnComponent as PawnComponent)?.CameraComponent as CameraComponent)?.Camera;
            if (c != null)
                rt = c.Transform;
        }

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
        if (HandleGpuExecutionModePropertyChanged(propName, prev, field))
            return;

        // Skip validation during active simulation to avoid triggering
        // SetupParticles which queues a rebuild every frame.
        if (!_isSimulating)
            OnValidate();
    }

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

    private static void RefreshPreparedParticleTree(ParticleTree pt)
    {
        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle particle = particles[i];
            Transform? transform = particle.Transform;
            if (transform is null)
            {
                particle.PreparedWorldChanged = particle.ParentIndex >= 0
                    && particle.ParentIndex < particleCount
                    && particles[particle.ParentIndex].PreparedWorldChanged;
                continue;
            }

            bool forceWorldRecalc = particle.ParentIndex >= 0
                && particle.ParentIndex < particleCount
                && particles[particle.ParentIndex].PreparedWorldChanged;
            particle.PreparedWorldChanged = transform.RecalculateMatrices(forceWorldRecalc, setRenderMatrixNow: false);
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

    private void Render()
    {
        if (!IsActiveInHierarchy || Engine.Rendering.State.IsShadowPass || !DebugDrawChains)
            return;

        if (UseGPU)
        {
            RenderGpuDebug();
            return;
        }

        for (int i = 0; i < _particleTrees.Count; ++i)
            DrawTree(_particleTrees[i]);
    }

    private void DrawTree(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"DrawTree:NoParticles:{FormatRoot(pt.Root)}", $"DrawTree skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        var particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle p = particles[i];
            if (p.ParentIndex >= 0 && p.ParentIndex < particleCount)
            {
                Particle p0 = particles[p.ParentIndex];
                Engine.Rendering.Debug.RenderLine(p.Position, p0.Position, ColorF4.White);

                float radius = p.Radius * _objectScale;
                if (radius > 0.0f)
                    DrawParticleRadiusSegment(p0.Position, p.Position, radius);
            }
            else if (p.ParentIndex >= 0)
            {
                LogFault($"DrawTree:BadParent:{FormatRoot(pt.Root)}:{p.ParentIndex}",
                    $"DrawTree invalid parent index {p.ParentIndex} for particle {i} (count={particleCount}, root={FormatRoot(pt.Root)}).");
            }
        }
    }

    private static void DrawParticleRadiusSegment(Vector3 start, Vector3 end, float radius)
    {
        if (radius <= 0.0f)
            return;

        float segmentLengthSquared = Vector3.DistanceSquared(start, end);
        if (segmentLengthSquared <= 1e-8f)
        {
            Engine.Rendering.Debug.RenderSphere(start, radius, false, ColorF4.Yellow);
            return;
        }

        Engine.Rendering.Debug.RenderCapsule(start, end, radius, false, ColorF4.Yellow);
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

        return MathF.Max(Engine.Time.Timer.Update.Delta, 1e-6f);
    }

    private void ResolveSimulationLoopAndTimeScale(float dt, out int loop, out float timeVar)
    {
        loop = 1;
        float stepDelta = dt;
        float updateRate = ResolveEffectiveUpdateRate();

        if ((UpdateMode != EUpdateMode.Default || _effectiveQualityTier != PhysicsChainQualityTier.Strict) && updateRate > 0.0f)
        {
            float frameTime = 1.0f / updateRate;
            _time += dt;
            loop = 0;

            while (_time >= frameTime)
            {
                _time -= frameTime;
                if (++loop >= 3)
                {
                    _time = 0.0f;
                    break;
                }
            }

            stepDelta = frameTime;
        }

        timeVar = ComputeSimulationTimeScale(stepDelta, ResolveSimulationReferenceDelta(), Speed);
    }

    private float ResolveEffectiveUpdateRate()
        => _effectiveQualityTier switch
        {
            PhysicsChainQualityTier.Hz30 => 30.0f,
            PhysicsChainQualityTier.Hz15 => 15.0f,
            PhysicsChainQualityTier.Hz7_5 => 7.5f,
            PhysicsChainQualityTier.Sleep => 0.0f,
            _ => UpdateRate,
        };

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
            pt.Root.RecalculateMatrixHierarchy(forceWorldRecalc: true, setRenderMatrixNow: false, childRecalcType: ELoopType.Parallel).Wait();
            AppendParticles(pt, pt.Root, -1, 0.0f);

            if (pt.Particles.Count == 0)
            {
                LogFault($"SetupParticles:ZeroParticles:{FormatRoot(pt.Root)}",
                    $"SetupParticles created zero particles for root {FormatRoot(pt.Root)}.");
            }
        }

        UpdateParameters();
        _particlesVersion++;
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
            }

            p.Damping = p.Damping.Clamp(0, 1);
            p.Elasticity = p.Elasticity.Clamp(0, 1);
            p.Stiffness = p.Stiffness.Clamp(0, 1);
            p.Inert = p.Inert.Clamp(0, 1);
            p.Friction = p.Friction.Clamp(0, 1);
            p.Radius = MathF.Max(p.Radius, 0);
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

            p.Transform.Translation = p.InitLocalPosition;
            p.Transform.Rotation = p.InitLocalRotation;
        }
    }

    private void ResetParticlesPosition()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ResetParticlesPosition(_particleTrees[i]);

        unchecked
        {
            ++_particleStateVersion;
        }

        _objectPrevPosition = Transform.WorldTranslation;
        _time = 0.0f;
        _preUpdateCount = 0;
        _fixedUpdateRenderAccumulatedTicks = 0L;
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
        if (UpdateMode == EUpdateMode.FixedUpdate)
        {
            long fixedDeltaTicks = Engine.Time.Timer.FixedUpdateDeltaTicks;
            long intervalTicks = fixedDeltaTicks;
            float fixedUpdateRate = ResolveEffectiveUpdateRate();
            if (fixedUpdateRate > 0.0f)
                intervalTicks = Math.Max(intervalTicks, (long)(TimeSpan.TicksPerSecond / fixedUpdateRate));
            return intervalTicks <= 0L
                ? 1.0f
                : Math.Clamp((float)(Math.Max(0L, _fixedUpdateRenderAccumulatedTicks) / (double)intervalTicks), 0.0f, 1.0f);
        }

        float updateRate = ResolveEffectiveUpdateRate();
        if (updateRate > 0.0f)
        {
            float frameTime = 1.0f / updateRate;
            return Math.Clamp(_time / frameTime, 0.0f, 1.0f);
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
                _fixedUpdateRenderAccumulatedTicks = 0L;

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

        Vector3 force = Gravity;
        float gravityLengthSquared = Gravity.LengthSquared();
        if (gravityLengthSquared > 1e-8f)
        {
            Vector3 fdir = Gravity / MathF.Sqrt(gravityLengthSquared);
            Vector3 pf = fdir * MathF.Max(Vector3.Dot(pt.RestGravity, fdir), 0.0f); // project current gravity to rest gravity
            force -= pf; // remove projected gravity
        }
        force = (force + Force) * (_objectScale * timeVar);

        Vector3 objectMove = loopIndex == 0 ? _objectMove : Vector3.Zero; // only first loop consider object move

        List<Particle> particles = pt.Particles;
        int particleCount = particles.Count;
        for (int i = 0; i < particleCount; ++i)
        {
            Particle p = particles[i];
            if (p.ParentIndex >= 0)
            {
                // verlet integration
                Vector3 v = p.Position - p.PrevPosition;
                Vector3 rmove = objectMove * p.Inert;
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

            float restLen = childPtcl.SegmentLength;

            // keep shape
            float stiffness = Interp.Lerp(1.0f, childPtcl.Stiffness, _weight);
            if (stiffness > 0 || childPtcl.Elasticity > 0)
            {
                Matrix4x4 m0 = parentPtcl.TransformLocalToWorldMatrix;
                m0.Translation = parentPtcl.Position;
                Vector3 restPos = childPtcl.Transform is not null 
                    ? Vector3.Transform(childPtcl.TransformLocalPosition, m0)
                    : Vector3.Transform(childPtcl.EndOffset, m0);
                Vector3 d = restPos - childPtcl.Position;
                childPtcl.Position += d * (childPtcl.Elasticity * timeVar);

                if (stiffness > 0)
                {
                    d = restPos - childPtcl.Position;
                    float len = d.Length();
                    float maxlen = restLen * (1.0f - stiffness) * 2.0f;
                    if (len > maxlen)
                        childPtcl.Position += d * ((len - maxlen) / len);
                }
            }

            // collide
            PhysicsChainColliderSnapshot[]? colliderSnapshots = _colliderSnapshotsForJob;
            int colliderSnapshotCount = _colliderSnapshotsForJobCount;
            if (colliderSnapshots is not null && colliderSnapshotCount > 0)
            {
                float particleRadius = childPtcl.Radius * _objectScale;
                for (int j = 0; j < colliderSnapshotCount; ++j)
                    childPtcl.IsColliding |= colliderSnapshots[j].Collide(ref childPtcl._position, particleRadius);
            }

            PhysicsChainColliderBase[]? fallbackColliders = _fallbackCollidersForJob;
            int fallbackColliderCount = _fallbackCollidersForJobCount;
            if (fallbackColliders is not null && fallbackColliderCount > 0)
            {
                float particleRadius = childPtcl.Radius * _objectScale;
                for (int j = 0; j < fallbackColliderCount; ++j)
                    childPtcl.IsColliding |= fallbackColliders[j].Collide(ref childPtcl._position, particleRadius);
            }

            // freeze axis, project to plane 
            if (FreezeAxis != EFreezeAxis.None)
            {
                Vector4 planeNormal = parentPtcl.TransformLocalToWorldMatrix.GetColumn((int)FreezeAxis - 1).Normalized();
                Plane movePlane = XRMath.CreatePlaneFromPointAndNormal(parentPtcl.Position, planeNormal.XYZ());
                childPtcl.Position -= movePlane.Normal * GeoUtil.DistanceFrom.PlaneToPoint(movePlane, childPtcl.Position);
            }

            // keep length
            Vector3 dd = parentPtcl.Position - childPtcl.Position;
            float leng = dd.Length();
            if (leng > 0)
                childPtcl.Position += dd * ((leng - restLen) / leng);
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

                float restLen = childPtcl.SegmentLength;

                //Keep shape
                float stiffness = Interp.Lerp(1.0f, childPtcl.Stiffness, _weight);
                if (stiffness > 0)
                {
                    Matrix4x4 m0 = parentPtcl.TransformLocalToWorldMatrix;
                    m0.Translation = parentPtcl.Position;
                    Vector3 restPos = childPtcl.Transform is not null 
                        ? Vector3.Transform(childPtcl.TransformLocalPosition, m0)
                        : Vector3.Transform(childPtcl.EndOffset, m0);
                    Vector3 d = restPos - childPtcl.Position;
                    float len = d.Length();
                    float maxlen = restLen * (1 - stiffness) * 2;
                    if (len > maxlen)
                        childPtcl.Position += d * ((len - maxlen) / len);
                }

                // keep length
                Vector3 dd = parentPtcl.Position - childPtcl.Position;
                float leng = dd.Length();
                if (leng > 0)
                    childPtcl.Position += dd * ((leng - restLen) / leng);
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
        using var profilerState = Profiler.Start("PhysicsChainComponent.ApplyParticlesToTransforms");

        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i]);
    }

    private void ApplyParticlesToTransforms(EInterpolationMode mode, float alpha)
    {
        using var profilerState = Profiler.Start("PhysicsChainComponent.ApplyParticlesToTransforms");

        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i], mode, alpha);
    }

    private static void ApplyParticlesToTransforms(ParticleTree pt)
        => ApplyParticlesToTransforms(pt, EInterpolationMode.Discrete, 1.0f);

    private static void ApplyParticlesToTransforms(ParticleTree pt, EInterpolationMode mode, float alpha)
    {
        if (pt.Particles is null || pt.Particles.Count <= 1)
        {
            LogFault($"ApplyParticlesToTransforms:NoParticles:{FormatRoot(pt.Root)}", $"ApplyParticlesToTransforms skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        var particles = pt.Particles;
        int particleCount = particles.Count;
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
            Vector3 parentPosition = ResolveFixedUpdateRenderPosition(parent.PreviousPhysicsPosition, parent.Position, mode, alpha);
            Vector3 childPosition = ResolveFixedUpdateRenderPosition(child.PreviousPhysicsPosition, child.Position, mode, alpha);

            if (parent.ChildCount <= 1 && pTfm is not null) // do not modify bone orientation if has more then one child
            {
                Vector3 localPos = cTfm is not null
                    ? child.InitLocalPosition
                    : child.EndOffset;

                Vector3 v0 = pTfm.TransformDirection(localPos);
                Vector3 v1 = childPosition - parentPosition;
                Quaternion rot = Quaternion.Normalize(XRMath.RotationBetweenVectors(v0, v1));

                pTfm.AddWorldRotationDelta(rot);

                // Refresh the parent's world + inverse-world matrices so that the
                // child's SetWorldTranslation (which uses ParentInverseWorldMatrix)
                // computes the correct local translation.
                pTfm.RecalculateMatrices(forceWorldRecalc: false);
            }

            cTfm?.SetWorldTranslation(childPosition);
        }
    }

}
