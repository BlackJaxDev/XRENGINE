using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Core.Reflection.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
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

    private float _speed = 1.0f;
    public float Speed
    {
        get => _speed;
        set => SetField(ref _speed, value);
    }

    public enum EUpdateMode
    {
        Normal,
        FixedUpdate,
        Undilated,
        Default
    }

    public enum EInterpolationMode
    {
        Discrete,
        Interpolate,
        Extrapolate
    }

    private EUpdateMode _updateMode = EUpdateMode.Default;
    public EUpdateMode UpdateMode
    {
        get => _updateMode;
        set => SetField(ref _updateMode, value);
    }

    private EInterpolationMode _interpolationMode = EInterpolationMode.Discrete;

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

    private bool _useGPU;
    private bool _useBatchedDispatcher = true;
    private bool _gpuSyncToBones = false;
    private volatile bool _hasPendingGpuBoneSync;
    private bool _debugDrawChains = true;

    private TransformBase? _rootBone = null;
    private float _rootInertia = 0.0f;
    private float _velocitySmoothing = 0.0f;

    private Vector3 _objectMove;
    private Vector3 _objectPrevPosition;
    private float _objectScale;
    private Vector3 _rootBonePrevPosition;
    private Vector3 _smoothedObjectMove;

    private float _time = 0;
    private float _weight = 1.0f;
    private bool _distantDisabled = false;
    private int _preUpdateCount = 0;
    private long _fixedUpdateRenderAccumulatedTicks;
    private bool _lastSimulationProducedResults;

    private readonly List<ParticleTree> _particleTrees = [];
    private readonly Dictionary<Transform, (Vector3 LocalPosition, Quaternion LocalRotation)> _initialLocalStates = [];

    // prepare data
    private float _deltaTime;
    private List<PhysicsChainColliderBase>? _effectiveColliders;

    // Thread-safe snapshots populated in Prepare() before jobs are scheduled.
    // Jobs read from these arrays instead of the mutable lists to avoid races.
    private PhysicsChainColliderBase[]? _collidersForJob;
    private int _collidersForJobCount;
    private ParticleTree[]? _particleTreesForJob;
    private int _particleTreesForJobCount;

    private static readonly ConcurrentQueue<PhysicsChainComponent> _pendingWorks = [];
    private static readonly List<PhysicsChainComponent> _effectiveWorks = [];
    private static readonly List<JobHandle> _scheduledWorkHandles = [];
    private static readonly object _executeWorksSync = new();

    private static int _updateCount;
    private static int _prepareFrame;

    public PhysicsChainComponent()
    {
        _gpuWorkRenderCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, ExecutePendingGpuWork);
        _gpuWorkRenderInfo = RenderInfo3D.New(this, _gpuWorkRenderCommand);
        RenderedObjects =
        [
            RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render)),
            _gpuWorkRenderInfo
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
    [Category("Execution")]
    [DisplayName("GPU Sync To Bones")]
    [Description("Compatibility mode that copies GPU particle results back to the CPU bone transforms asynchronously. Use the GPU-driven bone palette path when possible; enabling this adds extra staging, readback, and latency.")]
    public bool GpuSyncToBones
    {
        get => _gpuSyncToBones;
        set => SetField(ref _gpuSyncToBones, value);
    }
    public float DistanceToObject
    {
        get => _distanceToObject;
        set => SetField(ref _distanceToObject, value);
    }
    [Category("Execution")]
    [DisplayName("Fixed Update Transform Mode")]
    [Description("When Update Mode is FixedUpdate, controls how chain transforms are presented between fixed simulation ticks.")]
    public EInterpolationMode InterpolationMode
    {
        get => _interpolationMode;
        set => SetField(ref _interpolationMode, value);
    }

    [Category("Execution")]
    [DisplayName("Multithreaded")]
    [EditorBrowsableIf("!UseGPU")]
    public bool Multithread
    {
        get => _multithread;
        set => SetField(ref _multithread, value);
    }

    [Category("Execution")]
    [DisplayName("Use GPU")]
    [Description("Runs the physics chain through the GPU compute path instead of the CPU path.")]
    public bool UseGPU
    {
        get => _useGPU;
        set => SetField(ref _useGPU, value);
    }

    [Category("Execution")]
    [DisplayName("Use Batched Dispatcher")]
    [Description("When GPU mode is enabled, batch this chain with other GPU chains for one shared compute dispatch.")]
    [EditorBrowsableIf("UseGPU")]
    public bool UseBatchedDispatcher
    {
        get => _useBatchedDispatcher;
        set => SetField(ref _useBatchedDispatcher, value);
    }

    [Category("Debug")]
    [DisplayName("Draw Debug Chains")]
    [Description("Shows the debug chain overlay using white bone links and yellow radius capsules.")]
    public bool DebugDrawChains
    {
        get => _debugDrawChains;
        set => SetField(ref _debugDrawChains, value);
    }

    /// <summary>
    /// Optional root bone transform for character locomotion.
    /// When set, physics calculations can be made relative to this transform's movement
    /// instead of pure world space, which is useful for character controllers.
    /// </summary>
    public TransformBase? RootBone
    {
        get => _rootBone;
        set => SetField(ref _rootBone, value);
    }

    /// <summary>
    /// Controls how much the RootBone's movement affects physics calculations.
    /// 0 = World space (RootBone movement ignored), 1 = Fully relative to RootBone.
    /// This is useful for preventing physics chains from lagging behind when a character
    /// controller moves the character rapidly (e.g., teleporting, dashing).
    /// </summary>
    [Range(0, 1)]
    public float RootInertia
    {
        get => _rootInertia;
        set => SetField(ref _rootInertia, value);
    }

    /// <summary>
    /// Smooths the velocity applied to physics chains to reduce jitter at high velocities.
    /// 0 = No smoothing (raw velocity), 1 = Maximum smoothing (very dampened response).
    /// This helps prevent violent shaking when the root transform moves very fast.
    /// </summary>
    [Range(0, 1)]
    public float VelocitySmoothing
    {
        get => _velocitySmoothing;
        set => SetField(ref _velocitySmoothing, value);
    }
}
