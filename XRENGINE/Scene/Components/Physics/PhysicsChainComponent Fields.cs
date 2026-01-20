using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

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
    private AnimationCurve? _stiffnessDistrib = null;

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

    private bool _multithread = false;

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

    private static readonly ConcurrentQueue<PhysicsChainComponent> _pendingWorks = [];
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
}