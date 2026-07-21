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
