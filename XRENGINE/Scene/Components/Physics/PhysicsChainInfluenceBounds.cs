using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Conservative template-local influence sphere for one authored tree. The
/// runtime expands/transforms it for interpolation, velocity, and world pose.
/// </summary>
public readonly record struct PhysicsChainInfluenceBounds(Vector3 LocalCenter, float Radius)
{
    public bool IsValid => Radius >= 0.0f && float.IsFinite(Radius);
}
