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
    private bool _useGpuDrivenSkinning = true;
    private volatile bool _hasPendingGpuBoneSync;
    private bool _debugDrawChains;
    private PhysicsChainQualityTier _qualityTier = PhysicsChainQualityTier.Strict;
    private PhysicsChainQualityTier _effectiveQualityTier = PhysicsChainQualityTier.Strict;
    private PhysicsChainAutomaticRelevance _automaticQualityRelevance = PhysicsChainAutomaticRelevance.Relevant;
    private int _automaticQualityImportance = 50;
    private PhysicsChainPolicyControl _simulationPolicy = PhysicsChainPolicyControl.InheritTier;
    private PhysicsChainPolicyControl _collisionPolicy = PhysicsChainPolicyControl.InheritTier;
    private PhysicsChainOutputControl _palettePolicy = PhysicsChainOutputControl.InheritTier;
    private PhysicsChainOutputControl _boundsPolicy = PhysicsChainOutputControl.InheritTier;
    private PhysicsChainOutputControl _transformMirrorPolicy = PhysicsChainOutputControl.InheritTier;
    private PhysicsChainOffscreenBehavior _offscreenBehavior = PhysicsChainOffscreenBehavior.AutomaticByImportance;
    private int _offscreenDecayFrameCount = 45;
    private int _recentInteractionQualityFrameCount = 30;
    private int _recentInteractionQualityFramesRemaining;
    private int _offscreenQualityFrames;
    private bool _runtimeVisible = true;
    private bool _qualityPhaseInitialized;
    private float _qualityCadencePhase;
    private bool _enableAutomaticSleep = true;
    private float _sleepVelocityThreshold = 0.0005f;
    private float _sleepConstraintErrorThreshold = 0.0005f;
    private float _sleepRootAccelerationThreshold = 0.001f;
    private float _sleepExternalForceThreshold = 0.0005f;
    private float _sleepWakeThresholdMultiplier = 2.0f;
    private float _sleepTeleportDistance = 0.05f;
    private int _sleepRecentUseFrameCount = 2;
    private int _sleepQuietFrameCount = 30;
    private int _quietSimulationFrames;
    private int _recentUseFramesRemaining;
    private bool _isRuntimeSleeping;
    private Vector3 _sleepRootPosition;
    private Vector3 _sleepLastRootPosition;
    private Vector3 _sleepRootStep;
    private Vector3 _previousActivityRootMove;
    private int _sleepColliderSignature;
    private int _sleepColliderShapeSignature;
    private int _sleepColliderPoseSignature;
    private int _activityColliderShapeSignature;
    private int _activityColliderPoseSignature;
    private int _sleepConfiguredRootSignature;
    private PhysicsChainActivitySnapshot _lastActivitySnapshot;
    private PhysicsChainWakeReason _lastWakeReason;
    private ulong _wakeCount;

    private TransformBase? _rootBone = null;
    private float _rootInertia = 0.0f;
    private float _velocitySmoothing = 0.0f;

    private Vector3 _objectMove;
    private Vector3 _objectPrevPosition;
    private float _objectScale;
    private Vector3 _rootBonePrevPosition;
    private Vector3 _smoothedObjectMove;

    private float _time = 0;
    private float _qualityCadenceProgress;
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
    private readonly List<Transform> _configuredRootsScratch = [];
    private readonly HashSet<Transform> _configuredRootSetScratch = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Transform, int> _gpuDrivenParticleIndexByTransform = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, int> _gpuDrivenFirstChildIndexByParticle = [];
    private readonly Dictionary<int, Vector3> _gpuDrivenRestDirectionByParticle = [];

    // Thread-safe snapshots populated in Prepare() before jobs are scheduled.
    // Jobs read from these arrays instead of the mutable lists to avoid races.
    private PhysicsChainColliderSnapshot[]? _colliderSnapshotsForJob;
    private int _colliderSnapshotsForJobCount;
    private PhysicsChainColliderBase[]? _fallbackCollidersForJob;
    private int _fallbackCollidersForJobCount;
    private ParticleTree[]? _particleTreesForJob;
    private int _particleTreesForJobCount;

    private static int _prepareFrame;
    private PhysicsChainRuntimeHandle _runtimeHandle = PhysicsChainRuntimeHandle.Invalid;

    [Browsable(false)]
    public PhysicsChainRuntimeHandle RuntimeHandle => _runtimeHandle;

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
    [Category("Execution")]
    [DisplayName("GPU Sync To Bones")]
    [Description("Compatibility mode that copies GPU particle results back to the CPU bone transforms asynchronously. Use the GPU-driven bone palette path when possible; enabling this adds extra staging, readback, and latency.")]
    public bool GpuSyncToBones
    {
        get => _gpuSyncToBones;
        set => SetField(ref _gpuSyncToBones, value);
    }

    [Category("Execution")]
    [DisplayName("Use GPU-Driven Skinning")]
    [Description("Publishes simulated particle poses directly to matching skinned renderers. Disable this with GPU Sync To Bones when rendering must consume the CPU transform-updated palette.")]
    [EditorBrowsableIf("UseGPU")]
    public bool UseGpuDrivenSkinning
    {
        get => _useGpuDrivenSkinning;
        set => SetField(ref _useGpuDrivenSkinning, value);
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
    [Description("When GPU mode is enabled, share compute dispatches with compatible chains. Disabling this isolates the chain while retaining world-level render submission.")]
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

    [Category("Execution")]
    [DisplayName("Quality Tier")]
    [Description("Selects strict authored cadence, an explicit reduced cadence, sleep, or world-budgeted automatic cadence. Quality changes are never implicit when Strict is selected.")]
    public PhysicsChainQualityTier QualityTier
    {
        get => _qualityTier;
        set => SetField(ref _qualityTier, value);
    }

    [Category("Execution")]
    [DisplayName("Automatic Quality Relevance")]
    [Description("Selects the ideal cadence and demotion priority when Quality Tier is Automatic. Irrelevant chains may sleep; fixed tiers ignore this value.")]
    public PhysicsChainAutomaticRelevance AutomaticQualityRelevance
    {
        get => _automaticQualityRelevance;
        set => SetField(ref _automaticQualityRelevance, value);
    }

    [Category("Execution")]
    [DisplayName("Automatic Quality Importance")]
    [Description("Orders equally relevant automatic chains. Higher values are demoted later and promoted earlier.")]
    public int AutomaticQualityImportance
    {
        get => _automaticQualityImportance;
        set => SetField(ref _automaticQualityImportance, Math.Clamp(value, 0, 100));
    }

    [Category("Execution")]
    public PhysicsChainPolicyControl SimulationPolicy
    {
        get => _simulationPolicy;
        set => SetField(ref _simulationPolicy, value);
    }

    [Category("Execution")]
    public PhysicsChainPolicyControl CollisionPolicy
    {
        get => _collisionPolicy;
        set => SetField(ref _collisionPolicy, value);
    }

    [Category("Execution")]
    public PhysicsChainOutputControl PalettePolicy
    {
        get => _palettePolicy;
        set => SetField(ref _palettePolicy, value);
    }

    [Category("Execution")]
    public PhysicsChainOutputControl BoundsPolicy
    {
        get => _boundsPolicy;
        set => SetField(ref _boundsPolicy, value);
    }

    [Category("Execution")]
    public PhysicsChainOutputControl TransformMirrorPolicy
    {
        get => _transformMirrorPolicy;
        set => SetField(ref _transformMirrorPolicy, value);
    }

    [Category("Execution")]
    public PhysicsChainOffscreenBehavior OffscreenBehavior
    {
        get => _offscreenBehavior;
        set => SetField(ref _offscreenBehavior, value);
    }

    [Category("Execution")]
    public int OffscreenDecayFrameCount
    {
        get => _offscreenDecayFrameCount;
        set => SetField(ref _offscreenDecayFrameCount, Math.Max(value, 1));
    }

    [Category("Execution")]
    public int RecentInteractionQualityFrameCount
    {
        get => _recentInteractionQualityFrameCount;
        set => SetField(ref _recentInteractionQualityFrameCount, Math.Max(value, 0));
    }

    [Browsable(false)]
    public bool RuntimeVisible => _runtimeVisible;

    [Browsable(false)]
    public int OffscreenQualityFrames => _offscreenQualityFrames;

    [Browsable(false)]
    public float QualityCadencePhase => _qualityCadencePhase;

    [Browsable(false)]
    public PhysicsChainQualityTier EffectiveQualityTier => _effectiveQualityTier;

    [Browsable(false)]
    public PhysicsChainQualityPolicy EffectiveQualityPolicy
        => PhysicsChainQualityPolicy.Resolve(_effectiveQualityTier, UpdateRate).WithOverrides(
            SimulationPolicy,
            CollisionPolicy,
            PalettePolicy,
            BoundsPolicy,
            TransformMirrorPolicy);

    [Category("Execution")]
    public bool EnableAutomaticSleep
    {
        get => _enableAutomaticSleep;
        set => SetField(ref _enableAutomaticSleep, value);
    }

    [Category("Execution")]
    public float SleepVelocityThreshold
    {
        get => _sleepVelocityThreshold;
        set => SetField(ref _sleepVelocityThreshold, MathF.Max(value, 0.0f));
    }

    [Category("Execution")]
    public float SleepConstraintErrorThreshold
    {
        get => _sleepConstraintErrorThreshold;
        set => SetField(ref _sleepConstraintErrorThreshold, MathF.Max(value, 0.0f));
    }

    [Category("Execution")]
    public float SleepRootAccelerationThreshold
    {
        get => _sleepRootAccelerationThreshold;
        set => SetField(ref _sleepRootAccelerationThreshold, MathF.Max(value, 0.0f));
    }

    [Category("Execution")]
    public float SleepExternalForceThreshold
    {
        get => _sleepExternalForceThreshold;
        set => SetField(ref _sleepExternalForceThreshold, MathF.Max(value, 0.0f));
    }

    [Category("Execution")]
    public float SleepWakeThresholdMultiplier
    {
        get => _sleepWakeThresholdMultiplier;
        set => SetField(ref _sleepWakeThresholdMultiplier, MathF.Max(value, 1.01f));
    }

    [Category("Execution")]
    public float SleepTeleportDistance
    {
        get => _sleepTeleportDistance;
        set => SetField(ref _sleepTeleportDistance, MathF.Max(value, 0.0f));
    }

    [Category("Execution")]
    public int SleepRecentUseFrameCount
    {
        get => _sleepRecentUseFrameCount;
        set => SetField(ref _sleepRecentUseFrameCount, Math.Max(value, 0));
    }

    [Category("Execution")]
    public int SleepQuietFrameCount
    {
        get => _sleepQuietFrameCount;
        set => SetField(ref _sleepQuietFrameCount, Math.Max(value, 1));
    }

    [Browsable(false)]
    public bool IsRuntimeSleeping => _isRuntimeSleeping || _effectiveQualityTier == PhysicsChainQualityTier.Sleep;

    [Browsable(false)]
    public PhysicsChainActivitySnapshot LastActivitySnapshot => _lastActivitySnapshot;

    [Browsable(false)]
    public PhysicsChainWakeReason LastWakeReason => _lastWakeReason;

    [Browsable(false)]
    public ulong WakeCount => _wakeCount;

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
