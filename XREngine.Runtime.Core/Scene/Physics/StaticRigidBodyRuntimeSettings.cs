using XREngine.Components.Physics;

namespace XREngine.Scene.Physics;

/// <summary>Cached runtime settings applied to a backend static rigid body.</summary>
public readonly record struct StaticRigidBodyRuntimeSettings(
    bool GravityEnabled,
    bool SimulationEnabled,
    bool DebugVisualization,
    bool SendSleepNotifies,
    ushort CollisionGroup,
    PhysicsGroupsMask GroupsMask,
    byte DominanceGroup,
    byte PhysxOwnerClient,
    string? ActorName);

/// <summary>Optional backend sink for static-body settings beyond the portable creation contract.</summary>
public interface IStaticRigidBodySettingsSink
{
    void ApplyStaticRigidBodySettings(in StaticRigidBodyRuntimeSettings settings);
}