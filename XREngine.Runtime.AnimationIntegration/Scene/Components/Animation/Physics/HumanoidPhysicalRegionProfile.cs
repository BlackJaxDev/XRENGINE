using System;
using MemoryPack;

namespace XREngine.Components.Animation;

/// <summary>
/// Serializable PD-controller stiffness, damping, and blend-weight tuning profile
/// for one <see cref="EHumanoidBodyMask"/> region on <see cref="HumanoidPhysicalAnimationComponent"/>.
/// </summary>
[MemoryPackable]
public sealed partial class HumanoidPhysicalRegionProfile
{
    public EHumanoidBodyMask Region { get; set; } = EHumanoidBodyMask.Body;

    /// <summary>Proportional linear spring gain (1/s^2) pulling the physics body toward the animated bone position.</summary>
    public float LinearStiffness { get; set; } = 160.0f;

    /// <summary>Derivative linear damping gain (1/s) opposing linear velocity error.</summary>
    public float LinearDamping { get; set; } = 18.0f;

    /// <summary>Proportional angular spring gain (1/s^2) twisting the physics body toward the animated bone orientation.</summary>
    public float AngularStiffness { get; set; } = 120.0f;

    /// <summary>Derivative angular damping gain (1/s) opposing angular velocity error.</summary>
    public float AngularDamping { get; set; } = 14.0f;

    /// <summary>Region blend weight [0..1] (0 = pure ragdoll physics, 1 = full PD tracking).</summary>
    public float BlendWeight { get; set; } = 1.0f;

    /// <summary>Maximum linear correction speed (m/s) applied by the PD drive.</summary>
    public float MaxLinearSpeed { get; set; } = 25.0f;

    /// <summary>Maximum angular correction speed (rad/s) applied by the PD drive.</summary>
    public float MaxAngularSpeed { get; set; } = 22.0f;

    public float ClampedBlendWeight => float.IsFinite(BlendWeight) ? Math.Clamp(BlendWeight, 0.0f, 1.0f) : 1.0f;
}
