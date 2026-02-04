using Extensions;
using JoltPhysicsSharp;
using System;
using System.Numerics;
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Scene.Transforms;
using static XREngine.Engine;

namespace XREngine.Components;

public partial class PhysicsChainComponent : XRComponent, IRenderable
{
    private static readonly TimeSpan FaultLogInterval = TimeSpan.FromSeconds(2);

    private bool _isSimulating;
    private bool _rebuildQueued;
    private int _particlesVersion;

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

    protected internal override void OnComponentActivated()
    {
        SetupParticles();
        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);
        RegisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
        ResetParticlesPosition();
        InitializeRootBoneTracking();
        OnValidate();
    }
    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        InitTransforms();
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
        
        if (_preUpdateCount > 0 && Multithread)
            AddPendingWork(this);
        
        ++_updateCount;
    }

    private void LateUpdate()
    {
        if (_preUpdateCount == 0)
            return;

        if (_updateCount > 0)
        {
            _updateCount = 0;
            ++_prepareFrame;
        }

        _isSimulating = true;
        SetWeight(BlendWeight);

        if (!_pendingWorks.IsEmpty)
            ExecuteWorks();
        else
        {
            CheckDistance();
            if (IsNeedUpdate())
            {
                Prepare();
                UpdateParticles();
                ApplyParticlesToTransforms();
            }
        }

        _preUpdateCount = 0;
        _isSimulating = false;

        if (_rebuildQueued)
        {
            _rebuildQueued = false;
            SetupParticles();
            ResetParticlesPosition();
        }
    }

    private void Prepare()
    {
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

            // Ensure we sample the current (post-InitTransforms / post-animation) pose.
            // Without this, we can end up using stale world matrices from the prior frame,
            // effectively allowing the simulated pose to slowly become the new "rest".
            pt.Root.RecalculateMatrixHierarchy(forceWorldRecalc: true, setRenderMatrixNow: false, childRecalcType: ELoopType.Sequential).Wait();

            pt.RestGravity = pt.Root.TransformDirection(pt.LocalGravity);

            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                if (p.Transform is not null)
                {
                    p.TransformPosition = p.Transform.WorldTranslation;
                    p.TransformLocalPosition = p.Transform.LocalTranslation;
                    p.TransformLocalToWorldMatrix = p.Transform.WorldMatrix;
                }
            }
        }

        _effectiveColliders?.Clear();

        if (Colliders is null)
            return;
        
        for (int i = 0; i < Colliders.Count; ++i)
        {
            PhysicsChainColliderBase c = Colliders[i];
            if (c is null || !c.IsActive)
                continue;

            (_effectiveColliders ??= []).Add(c);
            if (c.PrepareFrame == _prepareFrame)
                continue;
            
            c.Prepare();
            c.PrepareFrame = _prepareFrame;
        }
    }

    private bool IsNeedUpdate()
        => _weight > 0 && !(DistantDisable && _distantDisabled);

    private void PreUpdate()
    {
        if (IsNeedUpdate())
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
            XRCamera? c = State.MainPlayer.ControlledPawn?.CameraComponent?.Camera;
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
        //OnValidate();
    }

    private void OnValidate()
    {
        UpdateRate = MathF.Max(UpdateRate, 0);
        Damping = Damping.Clamp(0, 1);
        Elasticity = Elasticity.Clamp(0, 1);
        Stiffness = Stiffness.Clamp(0, 1);
        Inert = Inert.Clamp(0, 1);
        Friction = Friction.Clamp(0, 1);
        Radius = MathF.Max(Radius, 0);

        if (!IsEditor || !IsPlaying)
            return;
        
        if (IsRootChanged())
        {
            InitTransforms();
            SetupParticles();
        }
        else
            UpdateParameters();
    }

    private bool IsRootChanged()
    {
        var roots = new List<Transform>();
        if (Root != null)
            roots.Add(Root);
        
        if (Roots != null)
            foreach (var root in Roots)
                if (root != null && !roots.Contains(root))
                    roots.Add(root);

        if (roots.Count == 0)
            roots.Add(SceneNode.GetTransformAs<Transform>(true)!);

        if (roots.Count != _particleTrees.Count)
            return true;

        for (int i = 0; i < roots.Count; ++i)
            if (roots[i] != _particleTrees[i].Root)
                return true;
        
        return false;
    }

    private void Render()
    {
        if (!IsActive || Engine.Rendering.State.IsShadowPass)
            return;

        if (IsEditor && !IsPlaying && Transform.HasChanged)
        {
            //InitTransforms();
            SetupParticles();
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

        Particle[] particles = [.. pt.Particles];
        for (int i = 0; i < particles.Length; ++i)
        {
            Particle p = particles[i];
            if (p.ParentIndex >= 0 && p.ParentIndex < particles.Length)
            {
                Particle p0 = particles[p.ParentIndex];
                Engine.Rendering.Debug.RenderLine(p.Position, p0.Position, ColorF4.Orange);
            }
            else if (p.ParentIndex >= 0)
            {
                LogFault($"DrawTree:BadParent:{FormatRoot(pt.Root)}:{p.ParentIndex}",
                    $"DrawTree invalid parent index {p.ParentIndex} for particle {i} (count={particles.Length}, root={FormatRoot(pt.Root)}).");
            }
            if (p.Radius > 0)
            {
                float radius = p.Radius * _objectScale;
                Engine.Rendering.Debug.RenderSphere(p.Position, radius, false, ColorF4.Yellow);
            }
        }
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

    private void UpdateParticles()
    {
        if (_particleTrees.Count <= 0)
            return;

        int loop = 1;
        float timeVar = 1.0f;
        float dt = _deltaTime;

        if (UpdateMode == EUpdateMode.Default)
        {
            if (UpdateRate > 0.0f)
                timeVar = dt * UpdateRate;
        }
        else if (UpdateRate > 0.0f)
        {
            float frameTime = 1.0f / UpdateRate;
            _time += dt;
            loop = 0;

            while (_time >= frameTime)
            {
                _time -= frameTime;
                if (++loop >= 3)
                {
                    _time = 0;
                    break;
                }
            }
        }
        
        if (loop > 0)
        {
            for (int i = 0; i < loop; ++i)
            {
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

        if (Root != null)
            AppendParticleTree(Root);
        
        if (Roots != null)
        {
            for (int i = 0; i < Roots.Count; ++i)
            {
                Transform root = Roots[i];
                if (root == null)
                    continue;

                if (_particleTrees.Exists(x => x.Root == root))
                    continue;

                AppendParticleTree(root);
            }
        }

        if (_particleTrees.Count == 0)
            AppendParticleTree(SceneNode.GetTransformAs<Transform>(true)!);

        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectPrevPosition = Transform.WorldTranslation;
        _objectMove = Vector3.Zero;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            AppendParticles(pt, pt.Root, -1, 0.0f);

            if (pt.Particles.Count == 0)
            {
                LogFault($"SetupParticles:ZeroParticles:{FormatRoot(pt.Root)}",
                    $"SetupParticles created zero particles for root {FormatRoot(pt.Root)}.");
            }
        }

        UpdateParameters();
        _particlesVersion++;
    }

    private void AppendParticleTree(Transform root)
    {
        if (root is null)
            return;

        _particleTrees.Add(new ParticleTree(root));
    }

    private void AppendParticles(ParticleTree tree, Transform? tfm, int parentIndex, float boneLength)
    {
        var ptcl = new Particle(tfm, parentIndex);

        if (tfm != null)
        {
            ptcl.Position = ptcl.PrevPosition = tfm.WorldTranslation;
            ptcl.InitLocalPosition = tfm.LocalTranslation;
            ptcl.InitLocalRotation = tfm.LocalRotation;
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
        {
            LogFault($"InitTransforms:NoParticles:{FormatRoot(pt.Root)}", $"InitTransforms skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        Particle[] particles = [.. pt.Particles];
        for (int i = 0; i < particles.Length; ++i)
        {
            Particle p = particles[i];
            if (p.Transform is null)
                continue;
            
            p.Transform.Translation = p.InitLocalPosition;
            p.Transform.Rotation = p.InitLocalRotation;
        }
    }

    private void ResetParticlesPosition()
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            ResetParticlesPosition(_particleTrees[i]);

        _objectPrevPosition = Transform.WorldTranslation;
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
    }

    private static void ResetParticlesPosition(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"ResetParticles:NoParticles:{FormatRoot(pt.Root)}", $"ResetParticlesPosition skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        Particle[] particles = [.. pt.Particles];
        for (int i = 0; i < particles.Length; ++i)
        {
            Particle p = particles[i];
            if (p.Transform is not null)
                p.Position = p.PrevPosition = p.Transform.WorldTranslation;
            else // end bone
            {
                if (p.ParentIndex < 0 || p.ParentIndex >= particles.Length)
                {
                    LogFault($"ResetParticles:BadParent:{FormatRoot(pt.Root)}:{p.ParentIndex}",
                        $"ResetParticlesPosition invalid parent index {p.ParentIndex} for particle {i} (count={particles.Length}, root={FormatRoot(pt.Root)}).");
                    continue;
                }

                Transform? pb = particles[p.ParentIndex].Transform;
                if (pb is not null)
                    p.Position = p.PrevPosition = pb.TransformPoint(p.EndOffset);
            }
            p.IsColliding = false;
        }
    }

    private void CalculateParticles(float timeVar, int loopIndex)
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            CalculateParticles(_particleTrees[i], timeVar, loopIndex);
    }

    private void CalculateParticles(ParticleTree pt, float timeVar, int loopIndex)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"CalculateParticles:NoParticles:{FormatRoot(pt.Root)}", $"CalculateParticles skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        Vector3 force = Gravity;
        Vector3 fdir = Gravity.Normalized();
        Vector3 pf = fdir * MathF.Max(Vector3.Dot(pt.RestGravity, fdir), 0); // project current gravity to rest gravity
        force -= pf; // remove projected gravity
        force = (force + Force) * (_objectScale * timeVar);

        Vector3 objectMove = loopIndex == 0 ? _objectMove : Vector3.Zero; // only first loop consider object move

        Particle[] particles = [.. pt.Particles];
        for (int i = 0; i < particles.Length; ++i)
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
        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticleTransforms(_particleTrees[i], timeVar);
    }

    private void ApplyParticleTransforms(ParticleTree pt, float timeVar)
    {
        if (pt.Particles is null || pt.Particles.Count <= 1)
        {
            LogFault($"ApplyParticleTransforms:NoParticles:{FormatRoot(pt.Root)}", $"ApplyParticleTransforms skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        Particle[] particles = [.. pt.Particles];
        for (int i = 1; i < particles.Length; ++i)
        {
            Particle childPtcl = particles[i];
            if (childPtcl.ParentIndex < 0 || childPtcl.ParentIndex >= particles.Length)
            {
                LogFault($"ApplyParticleTransforms:BadParent:{FormatRoot(pt.Root)}:{childPtcl.ParentIndex}",
                    $"ApplyParticleTransforms invalid parent index {childPtcl.ParentIndex} for particle {i} (count={particles.Length}, root={FormatRoot(pt.Root)}).");
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
            if (_effectiveColliders != null)
            {
                float particleRadius = childPtcl.Radius * _objectScale;
                for (int j = 0; j < _effectiveColliders.Count; ++j)
                {
                    PhysicsChainColliderBase c = _effectiveColliders[j];
                    childPtcl.IsColliding |= c.Collide(ref childPtcl._position, particleRadius);
                }
            }

            // freeze axis, project to plane 
            if (FreezeAxis != EFreezeAxis.None)
            {
                Vector4 planeNormal = parentPtcl.TransformLocalToWorldMatrix.GetColumn((int)FreezeAxis - 1).Normalized();
                Plane movePlane = XRMath.CreatePlaneFromPointAndNormal(parentPtcl.Position, planeNormal.XYZ());
                childPtcl.Position -= movePlane.Normal * GeoUtil.DistancePlanePoint(movePlane, childPtcl.Position);
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
        for (int i = 0; i < _particleTrees.Count; ++i)
            SkipUpdateParticles(_particleTrees[i]);
    }

    //Only update stiffness and keep bone length
    private void SkipUpdateParticles(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count == 0)
        {
            LogFault($"SkipUpdateParticles:NoParticles:{FormatRoot(pt.Root)}", $"SkipUpdateParticles skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        Particle[] particles = [.. pt.Particles];
        for (int i = 0; i < particles.Length; ++i)
        {
            Particle childPtcl = particles[i];
            if (childPtcl.ParentIndex >= 0)
            {
                childPtcl.PrevPosition += _objectMove;
                childPtcl.Position += _objectMove;

                if (childPtcl.ParentIndex >= particles.Length)
                {
                    LogFault($"SkipUpdateParticles:BadParent:{FormatRoot(pt.Root)}:{childPtcl.ParentIndex}",
                        $"SkipUpdateParticles invalid parent index {childPtcl.ParentIndex} for particle {i} (count={particles.Length}, root={FormatRoot(pt.Root)}).");
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
        for (int i = 0; i < _particleTrees.Count; ++i)
            ApplyParticlesToTransforms(_particleTrees[i]);
    }

    private static void ApplyParticlesToTransforms(ParticleTree pt)
    {
        if (pt.Particles is null || pt.Particles.Count <= 1)
        {
            LogFault($"ApplyParticlesToTransforms:NoParticles:{FormatRoot(pt.Root)}", $"ApplyParticlesToTransforms skipped: no particles. Root={FormatRoot(pt.Root)}.");
            return;
        }

        Particle[] particles = [.. pt.Particles];
        for (int i = 1; i < particles.Length; ++i)
        {
            Particle child = particles[i];
            if (child.ParentIndex < 0 || child.ParentIndex >= particles.Length)
            {
                LogFault($"ApplyParticlesToTransforms:BadParent:{FormatRoot(pt.Root)}:{child.ParentIndex}",
                    $"ApplyParticlesToTransforms invalid parent index {child.ParentIndex} for particle {i} (count={particles.Length}, root={FormatRoot(pt.Root)}).");
                continue;
            }

            Particle parent = particles[child.ParentIndex];

            Transform? pTfm = parent.Transform;
            Transform? cTfm = child.Transform;

            if (parent.ChildCount <= 1 && pTfm is not null) // do not modify bone orientation if has more then one child
            {
                Vector3 localPos = cTfm is not null
                    ? cTfm.Translation
                    : child.EndOffset;

                //pTfm.RecalculateMatrices(true, false);
                Vector3 v0 = pTfm.TransformDirection(localPos);
                Vector3 v1 = child.Position - parent.Position;
                Quaternion rot = Quaternion.Normalize(XRMath.RotationBetweenVectors(v0, v1));

                //pTfm.Parent?.RecalculateMatrices(true, false);
                pTfm.AddWorldRotationDelta(rot);
            }

            //pTfm?.RecalculateMatrices(true, false);
            //pTfm?.RecalculateInverseMatrices(true);
            cTfm?.SetWorldTranslation(child.Position);
        }
    }

    private static void AddPendingWork(PhysicsChainComponent db)
        => _pendingWorks.Enqueue(db);

    private static void AddWorkToQueue()
        => _workQueueSemaphore?.Release();

    private static PhysicsChainComponent? GetWorkFromQueue()
    {
        int idx = Interlocked.Increment(ref _workQueueIndex);
        return idx < 0 || idx >= _effectiveWorks.Count ? null : _effectiveWorks[idx];
    }

    private static void ThreadProc()
    {
        while (true)
        {
            _workQueueSemaphore?.WaitOne();

            GetWorkFromQueue()?.UpdateParticles();

            if (Interlocked.Decrement(ref _remainWorkCount) <= 0)
                _allWorksDoneEvent?.Set();
        }
    }

    private static void InitThreadPool()
    {
        _allWorksDoneEvent = new AutoResetEvent(false);
        _workQueueSemaphore = new Semaphore(0, int.MaxValue);

        int threadCount = Environment.ProcessorCount;

        for (int i = 0; i < threadCount; ++i)
        {
            var t = new Thread(ThreadProc)
            {
                IsBackground = true
            };
            t.Start();
        }
    }

    private static void ExecuteWorks()
    {
        if (_pendingWorks.IsEmpty)
            return;

        _effectiveWorks.Clear();

        while (_pendingWorks.TryDequeue(out PhysicsChainComponent? db))
        {
            if (db is null || !db.IsActive)
                continue;
            
            db.CheckDistance();
            if (db.IsNeedUpdate())
                _effectiveWorks.Add(db);
        }

        if (_effectiveWorks.Count <= 0)
            return;

        if (_allWorksDoneEvent == null)
            InitThreadPool();
        
        int workCount = _remainWorkCount = _effectiveWorks.Count;
        _workQueueIndex = -1;

        for (int i = 0; i < workCount; ++i)
        {
            PhysicsChainComponent db = _effectiveWorks[i];
            if (db is null)
                continue;
            db.Prepare();
            AddWorkToQueue();
        }

        _allWorksDoneEvent?.WaitOne();

        for (int i = 0; i < workCount; ++i)
            _effectiveWorks[i]?.ApplyParticlesToTransforms();
    }
}
