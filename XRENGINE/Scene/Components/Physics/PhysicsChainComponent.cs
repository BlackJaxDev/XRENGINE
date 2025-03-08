using Extensions;
using JoltPhysicsSharp;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Transforms;
using static XREngine.Engine;

namespace XREngine.Components;

public partial class PhysicsChainComponent : XRComponent, IRenderable
{
    private Transform? _root = null;
    public Transform? Root
    {
        get => _root;
        set => SetField(ref _root, value);
    }

    private List<Transform>? _roots = null;
    public List<Transform>? Roots
    {
        get => _roots;
        set => SetField(ref _roots, value);
    }

    private float _updateRate = 60.0f;
    public float UpdateRate
    {
        get => _updateRate;
        set => SetField(ref _updateRate, value);
    }

    public enum EUpdateMode
    {
        Normal,
        FixedUpdate,
        Undilated,
        Default
    }

    private EUpdateMode _updateMode = EUpdateMode.Default;
    public EUpdateMode UpdateMode
    {
        get => _updateMode;
        set => SetField(ref _updateMode, value);
    }

    private float _damping = 0.1f;
    private AnimationCurve? _dampingDistrib = null;

    private float _elasticity = 0.1f;
    private AnimationCurve? _elasticityDistrib = null;

    private float _stiffness = 0.1f;
    public AnimationCurve? _stiffnessDistrib = null;

    private float _inert = 0.0f;
    private AnimationCurve? _inertDistrib = null;

    private float _friction = 0.0f;
    private AnimationCurve? _frictionDistrib = null;

    private float _radius = 0.01f;
    private AnimationCurve? _radiusDistrib = null;

    private float _endLength = 0.0f;

    private Vector3 _endOffset = Vector3.Zero;
    private Vector3 _gravity = Vector3.Zero;
    private Vector3 _force = Vector3.Zero;

    private float _blendWeight = 1.0f;

    private List<PhysicsChainColliderBase>? _colliders = null;
    private List<TransformBase>? _exclusions = null;

    public enum EFreezeAxis
    {
        None,
        X,
        Y,
        Z
    }

    private EFreezeAxis _freezeAxis = EFreezeAxis.None;

    private bool _distantDisable = false;
    private Transform? _referenceObject = null;
    private float _distanceToObject = 20;

    private bool _multithread = true;

    private Vector3 _objectMove;
    private Vector3 _objectPrevPosition;
    private float _objectScale;

    private float _time = 0;
    private float _weight = 1.0f;
    private bool _distantDisabled = false;
    private int _preUpdateCount = 0;

    private readonly List<ParticleTree> _particleTrees = [];

    // prepare data
    private float _deltaTime;
    private List<PhysicsChainColliderBase>? _effectiveColliders;

    // multithread
    private bool _workAdded = false;

    private static readonly List<PhysicsChainComponent> _pendingWorks = [];
    private static readonly List<PhysicsChainComponent> _effectiveWorks = [];
    private static AutoResetEvent? _allWorksDoneEvent;
    private static int _remainWorkCount;
    private static Semaphore? _workQueueSemaphore;
    private static int _workQueueIndex;

    private static int _updateCount;
    private static int _prepareFrame;

    public PhysicsChainComponent()
    {
        RenderedObjects =
        [
            RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render))
        ];
    }

    public RenderInfo[] RenderedObjects { get; }

    [Range(0, 1)]
    public float Damping
    {
        get => _damping;
        set => SetField(ref _damping, value);
    }
    public AnimationCurve? DampingDistrib
    {
        get => _dampingDistrib;
        set => SetField(ref _dampingDistrib, value);
    }

    [Range(0, 1)]
    public float Elasticity
    {
        get => _elasticity;
        set => SetField(ref _elasticity, value);
    }
    public AnimationCurve? ElasticityDistrib
    {
        get => _elasticityDistrib;
        set => SetField(ref _elasticityDistrib, value);
    }

    [Range(0, 1)]
    public float Stiffness
    {
        get => _stiffness;
        set => SetField(ref _stiffness, value);
    }
    public AnimationCurve? StiffnessDistrib
    {
        get => _stiffnessDistrib;
        set => SetField(ref _stiffnessDistrib, value);
    }

    [Range(0, 1)]
    public float Inert
    {
        get => _inert;
        set => SetField(ref _inert, value);
    }
    public AnimationCurve? InertDistrib
    {
        get => _inertDistrib;
        set => SetField(ref _inertDistrib, value);
    }

    public float Friction
    {
        get => _friction;
        set => SetField(ref _friction, value);
    }
    public AnimationCurve? FrictionDistrib
    {
        get => _frictionDistrib;
        set => SetField(ref _frictionDistrib, value);
    }
    public float Radius
    {
        get => _radius;
        set => SetField(ref _radius, value);
    }
    public AnimationCurve? RadiusDistrib
    {
        get => _radiusDistrib;
        set => SetField(ref _radiusDistrib, value);
    }
    public float EndLength
    {
        get => _endLength;
        set => SetField(ref _endLength, value);
    }
    public Vector3 EndOffset
    {
        get => _endOffset;
        set => SetField(ref _endOffset, value);
    }
    public Vector3 Gravity
    {
        get => _gravity;
        set => SetField(ref _gravity, value);
    }
    public Vector3 Force
    {
        get => _force;
        set => SetField(ref _force, value);
    }
    [Range(0, 1)]
    public float BlendWeight
    {
        get => _blendWeight;
        set => SetField(ref _blendWeight, value);
    }
    public List<PhysicsChainColliderBase>? Colliders
    {
        get => _colliders;
        set => SetField(ref _colliders, value);
    }
    public List<TransformBase>? Exclusions
    {
        get => _exclusions;
        set => SetField(ref _exclusions, value);
    }
    public EFreezeAxis FreezeAxis
    {
        get => _freezeAxis;
        set => SetField(ref _freezeAxis, value);
    }
    public bool DistantDisable
    {
        get => _distantDisable;
        set => SetField(ref _distantDisable, value);
    }
    public Transform? ReferenceObject
    {
        get => _referenceObject;
        set => SetField(ref _referenceObject, value);
    }
    public float DistanceToObject
    {
        get => _distanceToObject;
        set => SetField(ref _distanceToObject, value);
    }
    public bool Multithread
    {
        get => _multithread;
        set => SetField(ref _multithread, value);
    }

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
        {
            AddPendingWork(this);
            _workAdded = true;
        }

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

        if (_workAdded)
        {
            _workAdded = false;
            ExecuteWorks();
        }
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

        _objectScale = MathF.Abs(Transform.LossyWorldScale.X);
        _objectMove = Transform.WorldTranslation - _objectPrevPosition;
        _objectPrevPosition = Transform.WorldTranslation;

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
                UpdateParticles1(timeVar, i);
                UpdateParticles2(timeVar);
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
            TransformBase? parent = tree.Particles[parentIndex].Transform;
            if (parent != null)
            {
                parent.RecalculateMatrices();
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
            ++tree.Particles[parentIndex].ChildCount;
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

            if (tfm.Children.Count == 0.0f && (EndLength > 0.0f || EndOffset != Vector3.Zero))
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
        pt.LocalGravity = (Vector3.Transform(Gravity, pt.RootWorldToLocalMatrix) - pt.RootWorldToLocalMatrix.Translation).Normalized() * Gravity.Length();

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

        _objectPrevPosition = Transform.WorldTranslation;
    }

    private static void ResetParticlesPosition(ParticleTree pt)
    {
        for (int i = 0; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            if (p.Transform is not null)
                p.Position = p.PrevPosition = p.Transform.WorldTranslation;
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

    private void UpdateParticles1(float timeVar, int loopIndex)
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            UpdateParticles1(_particleTrees[i], timeVar, loopIndex);
    }

    private void UpdateParticles1(ParticleTree pt, float timeVar, int loopIndex)
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
                p.Position += v * (1 - damping) + force + rmove;
            }
            else
            {
                p.PrevPosition = p.Position;
                p.Position = p.TransformPosition;
            }
        }
    }

    private void UpdateParticles2(float timeVar)
    {
        for (int i = 0; i < _particleTrees.Count; ++i)
            UpdateParticles2(_particleTrees[i], timeVar);
    }

    private void UpdateParticles2(ParticleTree pt, float timeVar)
    {
        for (int i = 1; i < pt.Particles.Count; ++i)
        {
            Particle p = pt.Particles[i];
            Particle p0 = pt.Particles[p.ParentIndex];

            float restLen = p.Transform is not null
                ? (p0.TransformPosition - p.TransformPosition).Length()
                : (Vector3.Transform(p.EndOffset, p0.TransformLocalToWorldMatrix) - p0.TransformLocalToWorldMatrix.Translation).Length();

            // keep shape
            float stiffness = Interp.Lerp(1.0f, p.Stiffness, _weight);
            if (stiffness > 0 || p.Elasticity > 0)
            {
                Matrix4x4 m0 = p0.TransformLocalToWorldMatrix;
                m0.Translation = p0.Position;
                Vector3 restPos = p.Transform is not null 
                    ? Vector3.Transform(p.TransformLocalPosition, m0)
                    : Vector3.Transform(p.EndOffset, m0);
                Vector3 d = restPos - p.Position;
                p.Position += d * (p.Elasticity * timeVar);

                if (stiffness > 0)
                {
                    d = restPos - p.Position;
                    float len = d.Length();
                    float maxlen = restLen * (1.0f - stiffness) * 2.0f;
                    if (len > maxlen && len > 0.0f)
                        p.Position += d * ((len - maxlen) / len);
                }
            }

            // collide
            if (_effectiveColliders != null)
            {
                float particleRadius = p.Radius * _objectScale;
                for (int j = 0; j < _effectiveColliders.Count; ++j)
                {
                    PhysicsChainColliderBase c = _effectiveColliders[j];
                    p.IsColliding |= c.Collide(ref p._position, particleRadius);
                }
            }

            // freeze axis, project to plane 
            if (FreezeAxis != EFreezeAxis.None)
            {
                Vector4 planeNormal = p0.TransformLocalToWorldMatrix.GetColumn((int)FreezeAxis - 1).Normalized();
                Plane movePlane = XRMath.CreatePlaneFromPointAndNormal(p0.Position, planeNormal.XYZ());
                p.Position -= movePlane.Normal * GeoUtil.DistancePlanePoint(movePlane, p.Position);
            }

            // keep length
            Vector3 dd = p0.Position - p.Position;
            float leng = dd.Length();
            if (leng > 0)
                p.Position += dd * ((leng - restLen) / leng);
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
            Particle p = pt.Particles[i];
            if (p.ParentIndex >= 0)
            {
                p.PrevPosition += _objectMove;
                p.Position += _objectMove;

                Particle p0 = pt.Particles[p.ParentIndex];

                float restLen = p.Transform is not null
                    ? (p0.TransformPosition - p.TransformPosition).Length()
                    : Vector3.Transform(p.EndOffset, p0.TransformLocalToWorldMatrix).Length();

                //Keep shape
                float stiffness = Interp.Lerp(1.0f, p.Stiffness, _weight);
                if (stiffness > 0)
                {
                    Matrix4x4 m0 = p0.TransformLocalToWorldMatrix;
                    m0.Translation = p0.Position;
                    Vector3 restPos = p.Transform is not null 
                        ? Vector3.Transform(p.TransformLocalPosition, m0)
                        : Vector3.Transform(p.EndOffset, m0);
                    Vector3 d = restPos - p.Position;
                    float len = d.Length();
                    float maxlen = restLen * (1 - stiffness) * 2;
                    if (len > maxlen)
                        p.Position += d * ((len - maxlen) / len);
                }

                // keep length
                Vector3 dd = p0.Position - p.Position;
                float leng = dd.Length();
                if (leng > 0)
                    p.Position += dd * ((leng - restLen) / leng);
            }
            else
            {
                p.PrevPosition = p.Position;
                p.Position = p.TransformPosition;
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

            if (parent.ChildCount <= 1 && parent.Transform is not null) // do not modify bone orientation if has more then one child
            {
                Vector3 localPos = child.Transform is not null
                    ? child.Transform.LocalTranslation
                    : child.EndOffset;

                parent.Transform.RecalculateMatrices();
                Vector3 v0 = parent.Transform.TransformVector(localPos);
                Vector3 v1 = child.Position - parent.Position;
                Quaternion rot = XRMath.RotationBetweenVectors(v0, v1);
                parent.Transform.Parent?.RecalculateMatrices();
                parent.Transform.SetWorldRotation(rot * parent.Transform.WorldRotation);
            }

            child.Transform?.SetWorldTranslation(child.Position);
        }
    }

    private static void AddPendingWork(PhysicsChainComponent db)
        => _pendingWorks.Add(db);

    private static void AddWorkToQueue(PhysicsChainComponent db)
        => _workQueueSemaphore?.Release();

    private static PhysicsChainComponent GetWorkFromQueue()
    {
        int idx = Interlocked.Increment(ref _workQueueIndex);
        return _effectiveWorks[idx];
    }

    private static void ThreadProc()
    {
        while (true)
        {
            _workQueueSemaphore?.WaitOne();

            PhysicsChainComponent db = GetWorkFromQueue();
            db.UpdateParticles();

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
        if (_pendingWorks.Count <= 0)
            return;

        _effectiveWorks.Clear();

        for (int i = 0; i < _pendingWorks.Count; ++i)
        {
            PhysicsChainComponent db = _pendingWorks[i];
            if (db != null && db.IsActive)
            {
                db.CheckDistance();
                if (db.IsNeedUpdate())
                    _effectiveWorks.Add(db);
            }
        }

        _pendingWorks.Clear();
        if (_effectiveWorks.Count <= 0)
            return;

        if (_allWorksDoneEvent == null)
            InitThreadPool();
        
        int workCount = _remainWorkCount = _effectiveWorks.Count;
        _workQueueIndex = -1;

        for (int i = 0; i < workCount; ++i)
        {
            PhysicsChainComponent db = _effectiveWorks[i];
            db.Prepare();
            AddWorkToQueue(db);
        }

        _allWorksDoneEvent?.WaitOne();

        for (int i = 0; i < workCount; ++i)
            _effectiveWorks[i].ApplyParticlesToTransforms();
    }
}
