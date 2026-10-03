namespace XREngine.Scene.Physics;

/// <summary>Exposes the scene that owns an actor created by a backend.</summary>
public interface IPhysicsSceneAttachedActor
{
    AbstractPhysicsScene? AttachedScene { get; }
}
