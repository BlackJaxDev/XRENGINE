using System.Numerics;

namespace XREngine.Components;

/// <summary>Conservative world-space bounds generated directly from backend state.</summary>
public readonly record struct PhysicsChainCpuBounds(Vector3 Minimum, Vector3 Maximum)
{
    public static PhysicsChainCpuBounds Invalid => new(
        new Vector3(float.PositiveInfinity),
        new Vector3(float.NegativeInfinity));

    public bool IsValid
        => float.IsFinite(Minimum.X) && float.IsFinite(Minimum.Y) && float.IsFinite(Minimum.Z)
        && float.IsFinite(Maximum.X) && float.IsFinite(Maximum.Y) && float.IsFinite(Maximum.Z)
        && Minimum.X <= Maximum.X && Minimum.Y <= Maximum.Y && Minimum.Z <= Maximum.Z;
}
