using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Data.Geometry;

/// <summary>
/// Reference-type adapter for a caller-owned <see cref="Box"/> scratch value.
/// </summary>
/// <remarks>
/// This avoids boxing a changing <see cref="Box"/> when a hot path requires an
/// <see cref="IVolume"/>. Instances are mutable and must not be retained beyond the
/// synchronous operation for which the owner prepared them.
/// </remarks>
public sealed class ReusableBoxVolume : IShape
{
    private Box _value;

    public void Set(in Box value)
        => _value = value;

    public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        => _value.ContainsAABB(box, tolerance);

    public EContainment ContainsBox(Box box)
        => _value.ContainsBox(box);

    public EContainment ContainsSphere(Sphere sphere)
        => _value.ContainsSphere(sphere);

    public EContainment ContainsCone(Cone cone)
        => _value.ContainsCone(cone);

    public EContainment ContainsCapsule(Capsule shape)
        => _value.ContainsCapsule(shape);

    public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        => _value.ClosestPoint(point, clampToEdge);

    public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        => _value.ContainsPoint(point, tolerance);

    public AABB GetAABB(bool transformed)
        => _value.GetAABB(transformed);

    public bool IntersectsSegment(Segment segment, out Vector3[] points)
        => _value.IntersectsSegment(segment, out points);

    public bool IntersectsSegment(Segment segment)
        => _value.IntersectsSegment(segment);
}
