using System.Numerics;
using XREngine.Components;
using XREngine.Networking;

namespace XREngine.Scene.Physics;

[Flags]
public enum PhysicsQueryActorTypes
{
    None = 0,
    Static = 1 << 0,
    Dynamic = 1 << 1,
    All = Static | Dynamic,
}

[Flags]
public enum PhysicsQueryHitDetail
{
    Default = 0,
    Position = 1 << 0,
    Normal = 1 << 1,
    UV = 1 << 2,
    FaceIndex = 1 << 3,
}

/// <summary>
/// Backend-neutral scene-query filtering contract.
/// </summary>
public interface IPhysicsQueryFilter
{
    PhysicsQueryActorTypes ActorTypes { get; }
    PhysicsQueryHitDetail HitDetail { get; }
    float SweepInflation { get; }
}

/// <summary>
/// Network identity and authority metadata shared by replicated physics components.
/// The replication layer owns handoff policy; backends only consume the resulting state.
/// </summary>
public interface IPhysicsReplicationTarget
{
    PhysicsReplicationAuthority ReplicationAuthority { get; set; }
    NetworkEntityId NetworkEntityId { get; set; }
    string? OwnerClientId { get; set; }
    int OwnerServerPlayerIndex { get; set; }
}

/// <summary>Receives lifecycle notifications for a backend-neutral physics joint.</summary>
public interface IPhysicsJointOwner
{
    void NotifyJointBroken();
}

/// <summary>Receives a fixed-step notification after a backend updates its body pose.</summary>
public interface IRuntimePhysicsStepListener
{
    void OnPhysicsStepped();
}

/// <summary>Exposes a component-owned dynamic rigid body to host-independent gameplay systems.</summary>
public interface IRuntimeDynamicRigidBodyComponent
{
    IAbstractDynamicRigidBody? RigidBody { get; }
}

/// <summary>Physics capabilities exposed by a runtime world without rendering ownership.</summary>
public interface IRuntimePhysicsWorldContext : IRuntimeWorldContext
{
    bool PhysicsEnabled { get; }
    AbstractPhysicsScene PhysicsScene { get; }
    float PhysicsResetMinYDistance { get; }
    Vector3 PhysicsGravity { get; }
    void EnqueuePhysicsResetFromMinYPlane(IAbstractDynamicRigidBody body);
}

public readonly struct PhysicsQueryFilter(
    PhysicsQueryActorTypes actorTypes = PhysicsQueryActorTypes.All,
    PhysicsQueryHitDetail hitDetail = PhysicsQueryHitDetail.Default,
    float sweepInflation = 0.0f) : AbstractPhysicsScene.IAbstractQueryFilter
{
    public PhysicsQueryActorTypes ActorTypes { get; } = actorTypes;
    public PhysicsQueryHitDetail HitDetail { get; } = hitDetail;
    public float SweepInflation { get; } = sweepInflation;
}

public interface IAbstractPhysicsActor
{
    void Destroy(bool wakeOnLostTouch = false);
}

public interface IAbstractStaticRigidBody : IAbstractRigidPhysicsActor
{
    XRComponent? OwningComponent { get; set; }
}

public interface IAbstractDynamicRigidBody : IAbstractRigidBody
{
    XRComponent? OwningComponent { get; set; }
    (Vector3 position, Quaternion rotation)? KinematicTarget { get; set; }
    bool GravityEnabled { get; set; }

    void SetTransform(Vector3 position, Quaternion rotation, bool wake = true);
    void SetLinearVelocity(Vector3 velocity, bool wake = true);
    void SetAngularVelocity(Vector3 velocity, bool wake = true);
    void WakeUp();
}

public interface IAbstractRigidPhysicsActor : IAbstractPhysicsActor
{
    (Vector3 position, Quaternion rotation) Transform { get; }
    Vector3 LinearVelocity { get; }
    Vector3 AngularVelocity { get; }
    bool IsSleeping { get; }
}

/// <summary>
/// Units used by a character-controller motion command.
/// </summary>
public enum CharacterMotionInputModel
{
    /// <summary>The value is a world-space velocity in units per second.</summary>
    Velocity = 0,

    /// <summary>The value is a world-space displacement for the command duration.</summary>
    Displacement = 1,
}

/// <summary>
/// Backend-neutral support state. Contact-location flags are reported separately.
/// </summary>
public enum CharacterSupportState
{
    Unknown,
    InAir,
    Supported,
    TooSteep,
    NotSupported,
}

[Flags]
public enum PhysicsCharacterControllerCapabilities
{
    None = 0,
    DisplacementInput = 1 << 0,
    VelocityInput = 1 << 1,
    ArbitraryUp = 1 << 2,
    MovingGround = 1 << 3,
    DynamicBodyInteraction = 1 << 4,
    CharacterVsCharacter = 1 << 5,
    QueryVisibility = 1 << 6,
    Materials = 1 << 7,
    InvisibleWalls = 1 << 8,
    ConstrainedClimbing = 1 << 9,
    PredictiveContacts = 1 << 10,
    IndependentCollisionTolerance = 1 << 11,
    FloorStickDistance = 1 << 12,
    IndependentStepDown = 1 << 13,
    CollisionFiltering = 1 << 14,
    SteepSlopeSliding = 1 << 15,
    MaximumStrength = 1 << 16,
    MaximumJumpHeight = 1 << 17,
    ScaleCoefficient = 1 << 18,
    VolumeGrowth = 1 << 19,
}

/// <summary>
/// A tagged movement sample produced on either the Update or PrePhysics thread.
/// </summary>
public readonly record struct CharacterMotionCommand(
    Vector3 Value,
    CharacterMotionInputModel InputModel,
    float MinDistance,
    float ElapsedTime);

/// <summary>
/// Optional neutral settings exposed only by controller backends that support
/// independent predictive contacts and extended stair/floor behavior.
/// </summary>
public interface IAdvancedCharacterControllerSettings
{
    float PredictiveContactDistance { get; set; }
    float CollisionTolerance { get; set; }
    float StickToFloorDistance { get; set; }
    float StepDownExtra { get; set; }
    float MaxStrength { get; set; }
}

/// <summary>
/// Runtime collision policy shared by controller backends. Implementations
/// apply changes on the next native physics step.
/// </summary>
public interface ICharacterControllerCollisionSettings
{
    LayerMask CollisionLayerMask { get; set; }
    bool SlideOnSteepSlopes { get; set; }
}

/// <summary>
/// Backend-neutral capsule character-controller surface used by gameplay code.
/// Backend-specific controller objects may expose richer extension APIs.
/// </summary>
public interface IAbstractCharacterController : IAbstractRigidPhysicsActor
{
    Vector3 Position { get; set; }
    Vector3 FootPosition { get; set; }
    Vector3 UpDirection { get; set; }

    float Radius { get; set; }
    float TotalHeight { get; }
    float SlopeLimit { get; set; }
    float StepOffset { get; set; }
    float ContactOffset { get; set; }

    CharacterMotionInputModel MotionInputModel { get; set; }
    PhysicsCharacterControllerCapabilities Capabilities { get; }
    CharacterSupportState SupportState { get; }
    bool IsGrounded { get; }
    bool CollidingUp { get; }
    bool CollidingDown { get; }
    bool CollidingSides { get; }
    Vector3 GroundNormal { get; }
    Vector3 GroundVelocity { get; }
    IAbstractRigidPhysicsActor? GroundActor { get; }
    CharacterMotionCommand LastMotionCommand { get; }
    Vector3 RequestedVelocity { get; }
    Vector3 EffectiveVelocity { get; }

    void SubmitMotion(in CharacterMotionCommand command);
    void Move(Vector3 value, float minDist, float elapsedTime);
    void Resize(float totalHeight);
    void Synchronize() { }
    void RequestRelease();
}

public interface IAbstractRigidBody : IAbstractRigidPhysicsActor
{
}
