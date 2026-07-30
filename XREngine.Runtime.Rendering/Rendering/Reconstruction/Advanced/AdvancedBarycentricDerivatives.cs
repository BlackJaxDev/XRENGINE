using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Perspective-correct weights and their analytical pixel-space gradients.
/// </summary>
public readonly record struct AdvancedBarycentricDerivatives(
    Vector3 Weights,
    Vector3 Dx,
    Vector3 Dy);
