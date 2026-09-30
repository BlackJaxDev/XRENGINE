using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Blittable dynamic input for one tree in a physics-chain template.
/// </summary>
public readonly record struct PhysicsChainCpuTreeInput(Vector3 RestGravity);
