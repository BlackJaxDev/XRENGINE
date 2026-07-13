using System.Numerics;
using XREngine.Components.Physics;
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
    StaticRigidBodyComponent? OwningComponent { get; set; }
}

public interface IAbstractDynamicRigidBody : IAbstractRigidBody
{
    DynamicRigidBodyComponent? OwningComponent { get; set; }
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
/// Backend-neutral capsule character-controller surface used by gameplay code.
/// Backend-specific controller objects may expose richer extension APIs.
/// </summary>
public interface IAbstractCharacterController : IAbstractRigidPhysicsActor
{
    Vector3 Position { get; set; }
    Vector3 FootPosition { get; set; }
    Vector3 UpDirection { get; set; }

    float Radius { get; set; }
    float Height { get; }
    float SlopeLimit { get; set; }
    float StepOffset { get; set; }
    float ContactOffset { get; set; }

    bool CollidingUp { get; }
    bool CollidingDown { get; }

    void Move(Vector3 delta, float minDist, float elapsedTime);
    void Resize(float height);
    void Synchronize() { }
    void RequestRelease();
}

public interface IAbstractRigidBody : IAbstractRigidPhysicsActor
{
}
