using System.Numerics;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics;

/// <summary>
/// Optional live body properties supported by a physics implementation.
/// </summary>
public interface IPhysicsRuntimeBodyProperties
{
    bool SimulationEnabled { get; set; }
    bool DebugVisualize { get; set; }
    bool SendSleepNotifies { get; set; }
    ushort CollisionGroup { get; set; }
    PhysicsGroupsMask GroupsMask { get; set; }
    byte DominanceGroup { get; set; }
    byte OwnerClient { get; set; }
    bool IsInScene { get; }
    string Name { get; set; }
    PhysicsRigidBodyFlags BodyFlags { get; set; }
    PhysicsLockFlags LockFlags { get; set; }
    float LinearDamping { get; set; }
    float AngularDamping { get; set; }
    float MaxLinearVelocity { get; set; }
    float MaxAngularVelocity { get; set; }
    float Mass { get; set; }
    Vector3 MassSpaceInertiaTensor { get; set; }
    PhysicsMassFrame CenterOfMassLocalPose { get; set; }
    float MinCcdAdvanceCoefficient { get; set; }
    float MaxDepenetrationVelocity { get; set; }
    float MaxContactImpulse { get; set; }
    float ContactSlopCoefficient { get; set; }
    float StabilizationThreshold { get; set; }
    float SleepThreshold { get; set; }
    float ContactReportThreshold { get; set; }
    float WakeCounter { get; set; }
    PhysicsSolverIterations SolverIterations { get; set; }
    (Vector3 position, Quaternion rotation)? KinematicTarget { get; set; }
    void ApplyShapeOffset(Vector3 translation, Quaternion rotation);
}
