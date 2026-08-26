using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Blittable authored/animated transform input for one flattened particle.
/// </summary>
public readonly record struct PhysicsChainCpuParticleInput(Matrix4x4 LocalToWorld);
