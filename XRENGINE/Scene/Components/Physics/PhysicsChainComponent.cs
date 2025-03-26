using Extensions;
using JoltPhysicsSharp;
using System.Numerics;
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
    protected internal override void OnComponentActivated()
    {
        SetupParticles();
        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, FixedUpdate);
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Update);
        RegisterTick(ETickGroup.Late, ETickOrder.Animation, LateUpdate);
        ResetParticlesPosition();
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

        Transform.RecalculateMatrices();
        var translation = Transform.WorldTranslation;
        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectMove = translation - _objectPrevPosition;
        _objectPrevPosition = translation;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            pt.Root.RecalculateMatrices();
            pt.RestGravity = pt.Root.TransformVector(pt.LocalGravity);

            for (int j = 0; j < pt.Particles.Count; ++j)
            {
                Particle p = pt.Particles[j];
                if (p.Transform is not null)
                {
                    p.Transform.RecalculateMatrices();
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
        float d2 = (rt.WorldTranslation - Transform.LocalTranslation).LengthSquared();
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
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.ParentIndex >= 0)
            {
                Particle p0 = pt.Particles[p.ParentIndex];
                Engine.Rendering.Debug.RenderLine(p.Position, p0.Position, ColorF4.Orange);
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

        Transform.RecalculateMatrices();
        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectPrevPosition = Transform.WorldTranslation;
        _objectMove = Vector3.Zero;

        for (int i = 0; i < _particleTrees.Count; ++i)
        {
            ParticleTree pt = _particleTrees[i];
            AppendParticles(pt, pt.Root, -1, 0.0f);
        }

        UpdateParameters();
    }

    private void AppendParticleTree(Transform root)
    {
        if (root is null)
            return;

        root.RecalculateInverseMatrices();
        _particleTrees.Add(new ParticleTree(root));
    }

    private void AppendParticles(ParticleTree tree, Transform? tfm, int parentIndex, float boneLength)
    {
        var ptcl = new Particle(tfm, parentIndex);

        if (tfm != null)
        {
            tfm.RecalculateMatrices();
            ptcl.Position = ptcl.PrevPosition = tfm.WorldTranslation;
            ptcl.InitLocalPosition = tfm.LocalTranslation;
            ptcl.InitLocalRotation = tfm.LocalRotation;
        }
        else //end bone
        {
            TransformBase? parent = tree.Particles[parentIndex].Transform;
            if (parent != null)
            {
                parent.RecalculateMatrices();
                parent.RecalculateInverseMatrices();
                if (EndLength > 0.0f)
                {
                    TransformBase? parentParentTfm = parent.Parent;
                    Vector3 endOffset;
                    if (parentParentTfm != null)
                    {
                        parentParentTfm.RecalculateMatrices();
                        endOffset = parent.InverseTransformPoint(parent.WorldTranslation * 2.0f - parentParentTfm.WorldTranslation) * EndLength;
                    }
                    else
                        endOffset = new Vector3(EndLength, 0.0f, 0.0f);
                    ptcl.EndOffset = endOffset;
                }
                else
                {
                    Transform.RecalculateMatrices();
                    ptcl.EndOffset = parent.InverseTransformPoint(Transform.TransformVector(EndOffset) + parent.WorldTranslation);
                }
                
                ptcl.Position = ptcl.PrevPosition = parent.TransformPoint(ptcl.EndOffset);
            }
            ptcl.InitLocalPosition = Vector3.Zero;
            ptcl.InitLocalRotation = Quaternion.Identity;
        }

        if (parentIndex >= 0 && tree.Particles[parentIndex].Transform is not null)
        {
            var parentPtcl = tree.Particles[parentIndex];
            var parentTfm = parentPtcl.Transform!;
            parentTfm.RecalculateMatrices();
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
        // m_LocalGravity = m_Root.InverseTransformDirection(m_Gravity);
        pt.LocalGravity = Vector3.TransformNormal(Gravity.Normalized(), pt.RootWorldToLocalMatrix).Normalized() * Gravity.Length();

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
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
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

        Transform.RecalculateMatrices();
        _objectPrevPosition = Transform.WorldTranslation;
    }

    private static void ResetParticlesPosition(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.Transform is not null)
            {
                p.Transform.RecalculateMatrices();
                p.Position = p.PrevPosition = p.Transform.WorldTranslation;
            }
            else // end bone
            {
                Transform? pb = pt.Particles[p.ParentIndex].Transform;
                if (pb is not null)
                {
                    pb.RecalculateMatrices();
                    p.Position = p.PrevPosition = pb.TransformPoint(p.EndOffset);
                }
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
        Vector3 force = Gravity;
        Vector3 fdir = Gravity.Normalized();
        Vector3 pf = fdir * MathF.Max(Vector3.Dot(pt.RestGravity, fdir), 0); // project current gravity to rest gravity
        force -= pf; // remove projected gravity
        force = (force + Force) * (_objectScale * timeVar);

        Vector3 objectMove = loopIndex == 0 ? _objectMove : Vector3.Zero; // only first loop consider object move

        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
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
        for (int i = 1; i < pt.Particles.Count; ++i)
        {
            Particle childPtcl = pt.Particles[i];
            Particle parentPtcl = pt.Particles[childPtcl.ParentIndex];

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
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle childPtcl = pt.Particles[i];
            if (childPtcl.ParentIndex >= 0)
            {
                childPtcl.PrevPosition += _objectMove;
                childPtcl.Position += _objectMove;

                Particle parentPtcl = pt.Particles[childPtcl.ParentIndex];

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
        for (int i = 1; i < pt.Particles.Count; ++i)
        {
            Particle child = pt.Particles[i];
            Particle parent = pt.Particles[child.ParentIndex];

            Transform? pTfm = parent.Transform;
            Transform? cTfm = child.Transform;
            cTfm?.RecalculateMatrices();
            if (parent.ChildCount <= 1 && pTfm is not null) // do not modify bone orientation if has more then one child
            {
                Vector3 localPos = cTfm is not null
                    ? cTfm.LocalTranslation
                    : child.EndOffset;

                pTfm.RecalculateMatrices();
                Vector3 v0 = pTfm.TransformVector(localPos);
                Vector3 v1 = child.Position - parent.Position;
                Quaternion rot = XRMath.RotationBetweenVectors(v0, v1);
                pTfm.Parent?.RecalculateInverseMatrices();
                pTfm.AddWorldRotation(rot);
            }

            pTfm?.RecalculateInverseMatrices();
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
