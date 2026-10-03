using System.Numerics;

namespace XREngine.Components;

public readonly record struct PhysicsChainAabb(Vector3 Minimum, Vector3 Maximum)
{
    public static PhysicsChainAabb Invalid => new(
        new Vector3(float.PositiveInfinity),
        new Vector3(float.NegativeInfinity));

    public bool IsValid
        => float.IsFinite(Minimum.X) && float.IsFinite(Minimum.Y) && float.IsFinite(Minimum.Z)
            && float.IsFinite(Maximum.X) && float.IsFinite(Maximum.Y) && float.IsFinite(Maximum.Z)
            && Minimum.X <= Maximum.X && Minimum.Y <= Maximum.Y && Minimum.Z <= Maximum.Z;

    public Vector3 Center => (Minimum + Maximum) * 0.5f;
    public Vector3 Extents => (Maximum - Minimum) * 0.5f;

    public bool Intersects(in PhysicsChainAabb other)
        => Minimum.X <= other.Maximum.X && Maximum.X >= other.Minimum.X
            && Minimum.Y <= other.Maximum.Y && Maximum.Y >= other.Minimum.Y
            && Minimum.Z <= other.Maximum.Z && Maximum.Z >= other.Minimum.Z;

    public PhysicsChainAabb Expanded(float amount)
    {
        Vector3 expansion = new(MathF.Max(amount, 0.0f));
        return new PhysicsChainAabb(Minimum - expansion, Maximum + expansion);
    }

    public static PhysicsChainAabb Union(in PhysicsChainAabb left, in PhysicsChainAabb right)
    {
        if (!left.IsValid)
            return right;
        if (!right.IsValid)
            return left;
        return new PhysicsChainAabb(Vector3.Min(left.Minimum, right.Minimum), Vector3.Max(left.Maximum, right.Maximum));
    }
}
