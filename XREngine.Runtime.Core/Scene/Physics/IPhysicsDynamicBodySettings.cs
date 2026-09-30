using XREngine.Components.Physics;

namespace XREngine.Scene.Physics;

/// <summary>
/// Optional runtime settings applied by dynamic body backends after creation.
/// </summary>
public interface IPhysicsDynamicBodySettings
{
    void SetCollisionFiltering(ushort collisionGroup, PhysicsGroupsMask groupsMask);
    void SetMotionQuality(PhysicsRigidBodyFlags flags);
    void SetLockFlags(PhysicsLockFlags flags);
    void SetDamping(float linear, float angular);
    void SetMass(float mass);
}
