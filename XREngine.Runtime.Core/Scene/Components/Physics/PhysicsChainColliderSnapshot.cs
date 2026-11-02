using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Immutable, worker-safe collider data used by the CPU solver. Common shapes
/// avoid virtual component dispatch and repeated transform/shape preparation.
/// </summary>
internal readonly struct PhysicsChainColliderSnapshot
{
    public required PhysicsChainColliderKind Kind { get; init; }
    public Vector3 Center { get; init; }
    public Vector3 End { get; init; }
    public Vector3 AxisX { get; init; }
    public Vector3 AxisY { get; init; }
    public Vector3 AxisZ { get; init; }
    public Vector3 HalfExtents { get; init; }
    public Vector3 PlaneNormal { get; init; }
    public float Radius { get; init; }
    public float InverseLengthSquared { get; init; }
    public float PlaneDistance { get; init; }
    public bool Inside { get; init; }
    /// <summary>Makes segment collision test only the child end of a segment.</summary>
    public bool ParticleContactOnly { get; init; }

    /// <summary>
    /// Gets the position along a segment, from 0 at <paramref name="start"/> to 1 at <paramref name="end"/>, that is closest to this collider.
    /// </summary>
    public float ClosestSegmentParameter(Vector3 start, Vector3 end)
    {
        if (ParticleContactOnly)
            return 1.0f;

        return Kind switch
        {
            PhysicsChainColliderKind.Sphere or PhysicsChainColliderKind.Box => ClosestParameterToPoint(start, end, Center),
            PhysicsChainColliderKind.Capsule => ClosestParameterToSegment(start, end, Center, End),
            PhysicsChainColliderKind.Plane => ClosestParameterToPlane(start, end, PlaneNormal, Inside),
            _ => 1.0f,
        };
    }

    /// <summary>Gets the position along a segment closest to a point.</summary>
    internal static float ClosestParameterToPoint(Vector3 start, Vector3 end, Vector3 point)
    {
        Vector3 direction = end - start;
        float lengthSquared = direction.LengthSquared();
        return lengthSquared > 1e-10f
            ? Math.Clamp(Vector3.Dot(point - start, direction) / lengthSquared, 0.0f, 1.0f)
            : 1.0f;
    }

    /// <summary>Gets the position along the first segment closest to the second segment.</summary>
    internal static float ClosestParameterToSegment(Vector3 start, Vector3 end, Vector3 otherStart, Vector3 otherEnd)
    {
        Vector3 direction = end - start;
        Vector3 otherDirection = otherEnd - otherStart;
        Vector3 offset = start - otherStart;
        float lengthSquared = direction.LengthSquared();
        float otherLengthSquared = otherDirection.LengthSquared();
        if (lengthSquared <= 1e-10f)
            return 1.0f;
        if (otherLengthSquared <= 1e-10f)
            return ClosestParameterToPoint(start, end, otherStart);

        float dot = Vector3.Dot(direction, otherDirection);
        float directionOffset = Vector3.Dot(direction, offset);
        float otherOffset = Vector3.Dot(otherDirection, offset);
        float denominator = lengthSquared * otherLengthSquared - dot * dot;
        float parameter = denominator > 1e-10f
            ? Math.Clamp((dot * otherOffset - directionOffset * otherLengthSquared) / denominator, 0.0f, 1.0f)
            : 0.0f;
        float otherParameter = (dot * parameter + otherOffset) / otherLengthSquared;
        if (otherParameter < 0.0f)
            parameter = Math.Clamp(-directionOffset / lengthSquared, 0.0f, 1.0f);
        else if (otherParameter > 1.0f)
            parameter = Math.Clamp((dot - directionOffset) / lengthSquared, 0.0f, 1.0f);
        return parameter;
    }

    /// <summary>Gets the end of a segment that is deepest behind a plane.</summary>
    internal static float ClosestParameterToPlane(Vector3 start, Vector3 end, Vector3 normal, bool inside)
    {
        float slope = Vector3.Dot(end - start, normal);
        return inside == (slope > 0.0f) ? 1.0f : 0.0f;
    }

    public bool Collide(ref Vector3 position, float particleRadius)
        => Kind switch
        {
            PhysicsChainColliderKind.Sphere => CollideSphere(ref position, Center, Radius + particleRadius),
            PhysicsChainColliderKind.Capsule => CollideCapsule(ref position, particleRadius),
            PhysicsChainColliderKind.Box => CollideBox(ref position, particleRadius),
            PhysicsChainColliderKind.Plane => CollidePlane(ref position, particleRadius),
            _ => false,
        };

    private static bool CollideSphere(ref Vector3 position, Vector3 center, float radius)
    {
        Vector3 delta = position - center;
        float distanceSquared = delta.LengthSquared();
        float radiusSquared = radius * radius;
        if (distanceSquared >= radiusSquared)
            return false;

        if (distanceSquared <= 1e-8f)
            delta = Vector3.UnitY;
        else
            delta *= radius / MathF.Sqrt(distanceSquared);

        position = center + delta;
        return true;
    }

    private bool CollideCapsule(ref Vector3 position, float particleRadius)
    {
        Vector3 direction = End - Center;
        if (InverseLengthSquared <= 0.0f)
            return CollideSphere(ref position, Center, Radius + particleRadius);

        float projection = Math.Clamp(Vector3.Dot(position - Center, direction) * InverseLengthSquared, 0.0f, 1.0f);
        Vector3 closest = Center + direction * projection;
        return CollideSphere(ref position, closest, Radius + particleRadius);
    }

    private bool CollideBox(ref Vector3 position, float particleRadius)
    {
        Vector3 delta = position - Center;
        Vector3 local = new(
            Vector3.Dot(delta, AxisX),
            Vector3.Dot(delta, AxisY),
            Vector3.Dot(delta, AxisZ));
        Vector3 clamped = Vector3.Clamp(local, -HalfExtents, HalfExtents);
        Vector3 closest = Center + AxisX * clamped.X + AxisY * clamped.Y + AxisZ * clamped.Z;
        Vector3 separation = position - closest;
        float distanceSquared = separation.LengthSquared();
        float radiusSquared = particleRadius * particleRadius;
        if (distanceSquared > radiusSquared)
            return false;

        if (distanceSquared > 1e-8f)
        {
            position = closest + separation * (particleRadius / MathF.Sqrt(distanceSquared));
            return true;
        }

        Vector3 faceDistance = HalfExtents - Vector3.Abs(local);
        if (faceDistance.X <= faceDistance.Y && faceDistance.X <= faceDistance.Z)
            position = Center + AxisX * MathF.CopySign(HalfExtents.X + particleRadius, local.X) + AxisY * local.Y + AxisZ * local.Z;
        else if (faceDistance.Y <= faceDistance.Z)
            position = Center + AxisX * local.X + AxisY * MathF.CopySign(HalfExtents.Y + particleRadius, local.Y) + AxisZ * local.Z;
        else
            position = Center + AxisX * local.X + AxisY * local.Y + AxisZ * MathF.CopySign(HalfExtents.Z + particleRadius, local.Z);
        return true;
    }

    private bool CollidePlane(ref Vector3 position, float particleRadius)
    {
        float distance = Vector3.Dot(PlaneNormal, position) + PlaneDistance;
        if (!Inside)
        {
            if (distance >= particleRadius)
                return false;
            position += PlaneNormal * (particleRadius - distance);
            return true;
        }

        if (distance <= -particleRadius)
            return false;
        position -= PlaneNormal * (distance + particleRadius);
        return true;
    }
}
