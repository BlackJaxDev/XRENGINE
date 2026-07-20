using System.Numerics;
using XREngine.Scene.Physics;

namespace XREngine.Components.Physics;

/// <summary>Base component for scene-owned backend-neutral physics actors.</summary>
public abstract class PhysicsActorComponent : XRComponent
{
    public abstract IAbstractPhysicsActor? PhysicsActor { get; }

    /// <summary>Raised after the backend actor reference changes, including creation and teardown.</summary>
    public event Action<PhysicsActorComponent, IAbstractPhysicsActor?, IAbstractPhysicsActor?>? PhysicsActorChanged;

    protected void NotifyPhysicsActorChanged(
        IAbstractPhysicsActor? previousActor,
        IAbstractPhysicsActor? currentActor)
        => PhysicsActorChanged?.Invoke(this, previousActor, currentActor);

    protected (Vector3 position, Quaternion rotation) GetSpawnPose()
    {
        Matrix4x4.Decompose(
            Transform.WorldMatrix,
            out _,
            out Quaternion rotation,
            out Vector3 translation);
        return (translation, rotation);
    }
}