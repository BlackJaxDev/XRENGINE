using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Components;

/// <summary>
/// Packed collision-event snapshot. Flags are backend-neutral event flags,
/// never native API enum values.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PhysicsChainCollisionEventReadbackValue(
    int ParticleIndex,
    int ColliderIndex,
    Vector3 Point,
    Vector3 Normal,
    float Impulse,
    int Flags);
