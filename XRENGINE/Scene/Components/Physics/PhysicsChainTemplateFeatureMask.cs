namespace XREngine.Components;

/// <summary>
/// Structural feature taxonomy used to bucket compatible CPU and GPU kernels.
/// </summary>
[Flags]
public enum PhysicsChainTemplateFeatureMask : uint
{
    None = 0u,
    BranchedTopology = 1u << 0,
    ParticleRadius = 1u << 1,
    Elasticity = 1u << 2,
    Stiffness = 1u << 3,
    Inertia = 1u << 4,
    Friction = 1u << 5,
    FreezeAxis = 1u << 6,
}
