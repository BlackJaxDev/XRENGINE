namespace XREngine.Scene.Physics;

/// <summary>Optional body exposed by a character controller implementation.</summary>
public interface IPhysicsControllerBodySource
{
    IAbstractDynamicRigidBody? Actor { get; }
}
