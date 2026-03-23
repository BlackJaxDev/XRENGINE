using Extensions;
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
        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);
        RegisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
        ResetParticlesPosition();
        InitializeRootBoneTracking();
        OnValidate();
    }
    protected override void OnComponentDeactivated()
    {
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
        
        if (!UseGPU && _preUpdateCount > 0 && Multithread)
            AddPendingWork(this);
        
        System.Threading.Interlocked.Increment(ref _updateCount);
    }

    private void LateUpdate()
    {
        if (_preUpdateCount == 0)
        {
            ApplyPendingGpuBoneSync();
            if (ShouldRefreshInterpolatedRenderPose())
                ApplyInterpolatedRenderPose();
            return;
        }

        if (System.Threading.Interlocked.Exchange(ref _updateCount, 0) > 0)
        {
            System.Threading.Interlocked.Increment(ref _prepareFrame);
        }

        _isSimulating = true;
        SetWeight(BlendWeight);

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
        }
        else if (Multithread)
            ExecuteWorks();
        else
        {
            CheckDistance();
            if (IsNeedUpdate())
            {
                Prepare();
                UpdateParticles();
                ApplyCurrentParticleTransforms(newSimulationResults: _lastSimulationProducedResults);
            }
        }

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

        // Effective colliders
        int colCount = _effectiveColliders?.Count ?? 0;
        if (colCount > 0)
        {
            if (_collidersForJob is null || _collidersForJob.Length < colCount)
                _collidersForJob = new PhysicsChainColliderBase[Math.Max(colCount, 4)];
            for (int i = 0; i < colCount; ++i)
                _collidersForJob[i] = _effectiveColliders![i];
        }
        _collidersForJobCount = colCount;
    }

    private bool IsNeedUpdate()
        => _weight > 0 && !(DistantDisable && _distantDisabled);

    private bool HasSimulatableParticles()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            if (_particleTrees[i].Particles is { Count: > 0 })
                return true;
        }

        return false;
    }

    private void PreUpdate()
    {
        if (IsNeedUpdate() && HasSimulatableParticles())
            InitTransforms();
        
        ++_preUpdateCount;
    }

    private void CheckDistance()
    {
        if (!DistantDisable)
            return;

        TransformBase? rt = ReferenceObject;
        if (rt is null)
        {
            XRCamera? c = (State.MainPlayer?.ControlledPawnComponent as PawnComponent)?.CameraComponent?.Camera;
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
            Transform fallbackRoot = SceneNode.GetTransformAs<Transform>(true)!;
            _configuredRootSetScratch.Add(fallbackRoot);
            _configuredRootsScratch.Add(fallbackRoot);
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
        if (UpdateRate > 0.0f)
            return 1.0f / UpdateRate;

        return MathF.Max(Engine.Time.Timer.Update.Delta, 1e-6f);
    }

    private void ResolveSimulationLoopAndTimeScale(float dt, out int loop, out float timeVar)
    {
        loop = 1;
        float stepDelta = dt;

        if (UpdateMode != EUpdateMode.Default && UpdateRate > 0.0f)
        {
            float frameTime = 1.0f / UpdateRate;
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

        List<Transform> roots = CollectConfiguredRoots();
        for (int i = 0; i < roots.Count; ++i)
            AppendParticleTree(roots[i]);

        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectPrevPosition = Transform.WorldTranslation;
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
        RebuildGpuDrivenRendererBindings();
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
            boneLength += (parentPtclPos - ptcl.Position).Length();
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
                || (UpdateMode != EUpdateMode.Default && UpdateRate > 0.0f));

    private bool ShouldRefreshInterpolatedRenderPose()
        => UsesInterpolatedPresentation() && (!UseGPU || GpuSyncToBones);

    private float ComputeRenderAlpha()
    {
        if (UpdateMode == EUpdateMode.FixedUpdate)
        {
            long fixedDeltaTicks = Engine.Time.Timer.FixedUpdateDeltaTicks;
            long intervalTicks = fixedDeltaTicks;
            if (UpdateRate > 0.0f)
                intervalTicks = Math.Max(intervalTicks, (long)(TimeSpan.TicksPerSecond / UpdateRate));
            return intervalTicks <= 0L
                ? 1.0f
                : Math.Clamp((float)(Math.Max(0L, _fixedUpdateRenderAccumulatedTicks) / (double)intervalTicks), 0.0f, 1.0f);
        }

        if (UpdateRate > 0.0f)
        {
            float frameTime = 1.0f / UpdateRate;
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

            float restLen = childPtcl.Transform is not null
                ? (parentPtcl.TransformPosition - childPtcl.TransformPosition).Length()
                : (Vector3.Transform(childPtcl.EndOffset, parentPtcl.TransformLocalToWorldMatrix) - parentPtcl.TransformLocalToWorldMatrix.Translation).Length();

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
            var colliders = _collidersForJob;
            int collidersCount = _collidersForJobCount;
            if (colliders != null && collidersCount > 0)
            {
                float particleRadius = childPtcl.Radius * _objectScale;
                for (int j = 0; j < collidersCount; ++j)
                {
                    PhysicsChainColliderBase? c = colliders[j];
                    if (c is not null)
                        childPtcl.IsColliding |= c.Collide(ref childPtcl._position, particleRadius);
                }
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

                float restLen = childPtcl.Transform is not null
                    ? (parentPtcl.TransformPosition - childPtcl.TransformPosition).Length()
                    : (Vector3.Transform(childPtcl.EndOffset, parentPtcl.TransformLocalToWorldMatrix) - parentPtcl.TransformLocalToWorldMatrix.Translation).Length();

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

    private static void RunParticleUpdateRange(List<PhysicsChainComponent> works, int startInclusive, int endExclusive)
    {
        for (int i = startInclusive; i < endExclusive; ++i)
            works[i].UpdateParticles();
    }

    private static void EnsureParallelWorkItemCapacity(int count)
    {
        if (_parallelWorkItems.Length >= count)
            return;

        int previousLength = _parallelWorkItems.Length;
        Array.Resize(ref _parallelWorkItems, count);
        for (int i = previousLength; i < _parallelWorkItems.Length; ++i)
            _parallelWorkItems[i] = new PhysicsChainBatchWorkItem();
    }

    private static void ExecuteParallelWorks(int workCount)
    {
        int sliceCount = Math.Min(workCount, Math.Max(Environment.ProcessorCount, 1));
        if (sliceCount <= 1)
        {
            RunParticleUpdateRange(_effectiveWorks, 0, workCount);
            return;
        }

        int queuedSliceCount = sliceCount - 1;
        EnsureParallelWorkItemCapacity(queuedSliceCount);

        int baseSliceSize = workCount / sliceCount;
        int remainder = workCount % sliceCount;
        int start = 0;
        int workItemIndex = 0;

        for (int sliceIndex = 0; sliceIndex < sliceCount; ++sliceIndex)
        {
            int sliceSize = baseSliceSize + (sliceIndex < remainder ? 1 : 0);
            int end = start + sliceSize;
            if (sliceIndex == 0)
            {
                RunParticleUpdateRange(_effectiveWorks, start, end);
            }
            else
            {
                PhysicsChainBatchWorkItem workItem = _parallelWorkItems[workItemIndex++];
                workItem.Configure(_effectiveWorks, start, end);
                ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false);
            }

            start = end;
        }

        Exception? firstFault = null;
        for (int i = 0; i < workItemIndex; ++i)
        {
            PhysicsChainBatchWorkItem workItem = _parallelWorkItems[i];
            workItem.Wait();
            firstFault ??= workItem.Fault;
        }

        if (firstFault is not null)
            throw new AggregateException("Physics chain parallel update failed.", firstFault);
    }

    private static void AddPendingWork(PhysicsChainComponent db)
        => _pendingWorks.Enqueue(db);

    private static void ExecuteWorks()
    {
        lock (_executeWorksSync)
        {
            if (_pendingWorks.IsEmpty)
                return;

            _effectiveWorks.Clear();
            _effectiveWorkSet.Clear();

            while (_pendingWorks.TryDequeue(out PhysicsChainComponent? db))
            {
                if (db is null || !db.IsActive)
                    continue;

                db.CheckDistance();
                if (db.IsNeedUpdate() && _effectiveWorkSet.Add(db))
                    _effectiveWorks.Add(db);
            }

            if (_effectiveWorks.Count <= 0)
                return;

            int workCount = _effectiveWorks.Count;

            // Prepare all components before scheduling any jobs.
            // This avoids a race where a running job reads shared collider
            // state that a later Prepare() call is still writing.
            for (int i = 0; i < workCount; ++i)
                _effectiveWorks[i]?.Prepare();

            if (JobManager.IsJobWorkerThread)
                RunParticleUpdateRange(_effectiveWorks, 0, workCount);
            else
                ExecuteParallelWorks(workCount);

            for (int i = 0; i < workCount; ++i)
            {
                PhysicsChainComponent? component = _effectiveWorks[i];
                if (component is null)
                    continue;
                component.ApplyCurrentParticleTransforms(newSimulationResults: component._lastSimulationProducedResults);
                // Mark as handled so the component's own LateUpdate does not re-simulate.
                component._preUpdateCount = 0;
            }
        }
    }

    private sealed class PhysicsChainBatchWorkItem
    {
        private readonly ManualResetEventSlim _completed = new(true);
        private List<PhysicsChainComponent>? _works;
        private int _startInclusive;
        private int _endExclusive;

        public Exception? Fault { get; private set; }

        public void Configure(List<PhysicsChainComponent> works, int startInclusive, int endExclusive)
        {
            _works = works;
            _startInclusive = startInclusive;
            _endExclusive = endExclusive;
            Fault = null;
            _completed.Reset();
        }

        public void Run()
        {
            try
            {
                List<PhysicsChainComponent>? works = _works;
                if (works is not null)
                    RunParticleUpdateRange(works, _startInclusive, _endExclusive);
            }
            catch (Exception ex)
            {
                Fault = ex;
            }
            finally
            {
                _completed.Set();
            }
        }

        public void Wait()
            => _completed.Wait();
    }
}
