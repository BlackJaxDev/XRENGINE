using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Physics;

[RequiresTransform(typeof(RigidBodyTransform))]
[Category("Physics")]
[DisplayName("Static Rigid Body")]
[Description("Fixed collider that participates in the physics simulation without moving.")]
[XRComponentEditor("XREngine.Editor.ComponentEditors.StaticRigidBodyComponentEditor")]
public class StaticRigidBodyComponent : PhysicsActorComponent, IPhysicsReplicationTarget
{
    private int _ownershipSyncDepth;
    private IAbstractStaticRigidBody? _rigidBody;
    private AbstractPhysicsScene? _registeredScene;
    private bool _autoCreateRigidBody = true;
    private bool _gravityEnabled = true;
    private bool _simulationEnabled = true;
    private bool _debugVisualization;
    private bool _sendSleepNotifies;
    private ushort _collisionGroup;
    private PhysicsGroupsMask _groupsMask = PhysicsGroupsMask.Empty;
    private byte _dominanceGroup;
    private byte _physxOwnerClient;
    private PhysicsReplicationAuthority _replicationAuthority = PhysicsReplicationAuthority.LocalSimulation;
    private NetworkEntityId _networkEntityId;
    private string? _ownerClientId;
    private int _ownerServerPlayerIndex = -1;
    private string? _actorName;
    private AbstractPhysicsMaterial? _material;
    private PhysicsMaterialDefinition? _materialDefinition;
    private IPhysicsGeometry? _geometry;
    private List<PhysicsColliderShape> _colliderShapes = [];
    private Vector3 _shapeOffsetTranslation;
    private Quaternion _shapeOffsetRotation = Quaternion.Identity;
    private bool _autoGenerateConvexCollidersFromSiblingModel;
    private WeakReference<XRComponent>? _targetModelComponentRef;
    private IReadOnlyList<XRComponent>? _targetModelComponents;

    public RigidBodyTransform RigidBodyTransform => SceneNode.GetTransformAs<RigidBodyTransform>(true)!;

    [Browsable(false), RuntimeOnly]
    public IAbstractStaticRigidBody? RigidBody { get => _rigidBody; set => SetField(ref _rigidBody, value); }
    [Category("Initialization")]
    public bool AutoCreateRigidBody { get => _autoCreateRigidBody; set => SetField(ref _autoCreateRigidBody, value); }
    [Category("Shape")]
    public AbstractPhysicsMaterial? Material { get => _material; set => SetField(ref _material, value); }
    [Category("Shape")]
    public PhysicsMaterialDefinition? MaterialDefinition { get => _materialDefinition; set => SetField(ref _materialDefinition, value); }
    [Category("Shape")]
    public List<PhysicsColliderShape> ColliderShapes { get => _colliderShapes; set => SetField(ref _colliderShapes, value ?? []); }
    [Category("Shape")]
    public IPhysicsGeometry? Geometry { get => _geometry; set => SetField(ref _geometry, value); }
    [Category("Shape")]
    public Vector3 ShapeOffsetTranslation { get => _shapeOffsetTranslation; set => SetField(ref _shapeOffsetTranslation, value); }
    [Category("Shape")]
    public Quaternion ShapeOffsetRotation { get => _shapeOffsetRotation; set => SetField(ref _shapeOffsetRotation, value); }
    [Category("Shape")]
    public bool AutoGenerateConvexCollidersFromSiblingModel
    {
        get => _autoGenerateConvexCollidersFromSiblingModel;
        set => SetField(ref _autoGenerateConvexCollidersFromSiblingModel, value);
    }

    [Browsable(false)]
    public XRComponent? TargetModelComponent
    {
        get => _targetModelComponentRef is not null && _targetModelComponentRef.TryGetTarget(out XRComponent? target) ? target : null;
        set => SetField(ref _targetModelComponentRef, value is null ? null : new WeakReference<XRComponent>(value));
    }

    [Browsable(false)]
    public IReadOnlyList<XRComponent>? TargetModelComponents
    {
        get => _targetModelComponents;
        set => SetField(ref _targetModelComponents, value);
    }
    [Category("Forces")]
    public bool GravityEnabled { get => _gravityEnabled; set { if (SetField(ref _gravityEnabled, value)) ApplyCachedProperties(); } }
    [Category("Simulation")]
    public bool SimulationEnabled { get => _simulationEnabled; set { if (SetField(ref _simulationEnabled, value)) ApplyCachedProperties(); } }
    [Category("Debug")]
    public bool DebugVisualization { get => _debugVisualization; set { if (SetField(ref _debugVisualization, value)) ApplyCachedProperties(); } }
    [Category("Sleep")]
    public bool SendSleepNotifies { get => _sendSleepNotifies; set { if (SetField(ref _sendSleepNotifies, value)) ApplyCachedProperties(); } }
    [Category("Collision")]
    public ushort CollisionGroup { get => _collisionGroup; set { if (SetField(ref _collisionGroup, value)) ApplyCachedProperties(); } }
    [Category("Collision")]
    public PhysicsGroupsMask GroupsMask { get => _groupsMask; set { if (SetField(ref _groupsMask, value)) ApplyCachedProperties(); } }
    [Category("Collision")]
    public byte DominanceGroup { get => _dominanceGroup; set { if (SetField(ref _dominanceGroup, value)) ApplyCachedProperties(); } }
    [Category("Physics / PhysX Extensions")]
    public byte PhysxOwnerClient { get => _physxOwnerClient; set { if (SetField(ref _physxOwnerClient, value)) ApplyCachedProperties(); } }
    [Category("Debug")]
    public string? ActorName { get => _actorName; set { if (SetField(ref _actorName, value)) ApplyCachedProperties(); } }
    [Category("Networking")]
    public PhysicsReplicationAuthority ReplicationAuthority { get => _replicationAuthority; set => SetField(ref _replicationAuthority, value); }
    [Category("Networking")]
    public NetworkEntityId NetworkEntityId { get => _networkEntityId; set => SetField(ref _networkEntityId, value); }
    [Category("Networking")]
    public string? OwnerClientId { get => _ownerClientId; set => SetField(ref _ownerClientId, value); }
    [Category("Networking")]
    public int OwnerServerPlayerIndex { get => _ownerServerPlayerIndex; set => SetField(ref _ownerServerPlayerIndex, value); }
    [Browsable(false)]
    public override IAbstractPhysicsActor? PhysicsActor => RigidBody;

    internal void SetRigidBodyFromRigidBodyOwner(IAbstractStaticRigidBody? body)
    {
        try { _ownershipSyncDepth++; RigidBody = body; }
        finally { _ownershipSyncDepth--; }
    }

    public void RebuildCollisionShapes(bool wakeOnLostTouch = true)
    {
        IAbstractStaticRigidBody? oldBody = RigidBody;
        AbstractPhysicsScene? scene = GetPhysicsScene();
        if (oldBody is not null && scene is not null
            && scene.BackendService.TryReplaceCollisionShapes(oldBody, BuildCreateInfo()))
            return;
        RigidBody = null;
        oldBody?.Destroy(wakeOnLostTouch);
        EnsureRigidBodyConstructed();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        bool hadBody = RigidBody is not null;
        EnsureRigidBodyConstructed();
        if (hadBody)
            ApplyCachedPropertiesOnPhysicsThread();
        TryRegisterRigidBodyWithScene();
        RuntimeStaticColliderAuthoringServices.Current.OnActivated(this);
    }

    protected override void OnComponentDeactivated()
    {
        RemoveRigidBodyFromScene();
        base.OnComponentDeactivated();
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (!change || propName != nameof(RigidBody) || RigidBody is null)
            return change;
        RemoveRigidBodyFromScene();
        if (_ownershipSyncDepth == 0 && RigidBody.OwningComponent == this)
            RigidBody.OwningComponent = null;
        if (RigidBodyTransform.RigidBody == RigidBody)
            RigidBodyTransform.RigidBody = null;
        return true;
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName != nameof(RigidBody))
            return;
        NotifyPhysicsActorChanged(prev as IAbstractPhysicsActor, RigidBody);
        if (RigidBody is null)
            return;
        if (_ownershipSyncDepth == 0)
            RigidBody.OwningComponent = this;
        RigidBodyTransform.RigidBody = RigidBody;
        ApplyCachedProperties();
        TryRegisterRigidBodyWithScene();
    }

    private AbstractPhysicsScene? GetPhysicsScene()
        => World is IRuntimePhysicsWorldContext physicsWorld ? physicsWorld.PhysicsScene : null;

    private void EnsureRigidBodyConstructed()
    {
        AbstractPhysicsScene? scene = GetPhysicsScene();
        if (!AutoCreateRigidBody || RigidBody is not null || scene is null)
            return;
        RigidBody = scene.BackendService.CreateStaticRigidBody(BuildCreateInfo());
    }

    private PhysicsRigidBodyCreateInfo BuildCreateInfo()
        => new(ColliderShapes, Geometry, Material, MaterialDefinition, GetSpawnPose(),
            ShapeOffsetTranslation, ShapeOffsetRotation, 0.0f,
            CollisionGroup == 0 ? new LayerMask(1) : new LayerMask(1 << CollisionGroup))
        { GravityEnabled = GravityEnabled };

    private StaticRigidBodyRuntimeSettings BuildSettings()
        => new(_gravityEnabled, _simulationEnabled, _debugVisualization, _sendSleepNotifies,
            _collisionGroup, _groupsMask, _dominanceGroup, _physxOwnerClient, _actorName);

    private void ApplyCachedProperties()
    {
        if (RigidBody is IStaticRigidBodySettingsSink sink)
            sink.ApplyStaticRigidBodySettings(BuildSettings());
    }

    private void ApplyCachedPropertiesOnPhysicsThread()
    {
        if (RuntimePhysicsServices.Current.IsPhysicsThread) { ApplyCachedProperties(); return; }
        RuntimeThreadServices.Current.EnqueuePhysicsThread(() =>
        {
            if (IsActive && RigidBody is not null)
                ApplyCachedProperties();
        });
    }

    private void TryRegisterRigidBodyWithScene()
    {
        if (!IsActive || RigidBody is null)
            return;
        AbstractPhysicsScene? scene = GetPhysicsScene();
        if (scene is null || ReferenceEquals(_registeredScene, scene))
            return;
        _registeredScene?.RemoveActor(RigidBody);
        scene.AddActor(RigidBody);
        _registeredScene = scene;
    }

    private void RemoveRigidBodyFromScene()
    {
        if (RigidBody is not null)
            _registeredScene?.RemoveActor(RigidBody);
        _registeredScene = null;
    }
}