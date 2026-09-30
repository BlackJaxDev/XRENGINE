using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Compact blittable collider input for the scalar CPU backend. Shape factories
/// precompute the capsule projection term and keep dispatch free of components
/// and virtual calls.
/// </summary>
public readonly record struct PhysicsChainCpuCollider
{
    private const float Epsilon = 1e-8f;

    public PhysicsChainCpuColliderKind Kind { get; init; }
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
    public uint Inside { get; init; }

    public static PhysicsChainCpuCollider Sphere(Vector3 center, float radius)
        => new()
        {
            Kind = PhysicsChainCpuColliderKind.Sphere,
            Center = center,
            Radius = radius,
        };

    public static PhysicsChainCpuCollider Capsule(Vector3 start, Vector3 end, float radius)
    {
        float lengthSquared = Vector3.DistanceSquared(start, end);
        return new PhysicsChainCpuCollider
        {
            Kind = PhysicsChainCpuColliderKind.Capsule,
            Center = start,
            End = end,
            Radius = radius,
            InverseLengthSquared = float.IsFinite(lengthSquared) && lengthSquared > Epsilon
                ? 1.0f / lengthSquared
                : 0.0f,
        };
    }

    public static PhysicsChainCpuCollider Box(
        Vector3 center,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        Vector3 halfExtents)
        => new()
        {
            Kind = PhysicsChainCpuColliderKind.Box,
            Center = center,
            AxisX = axisX,
            AxisY = axisY,
            AxisZ = axisZ,
            HalfExtents = halfExtents,
        };

    public static PhysicsChainCpuCollider Plane(Vector3 normal, float distance, bool inside)
        => new()
        {
            Kind = PhysicsChainCpuColliderKind.Plane,
            PlaneNormal = normal,
            PlaneDistance = distance,
            Inside = inside ? 1u : 0u,
        };

    public bool TryCollide(ref Vector3 position, float particleRadius)
    {
        if (!IsFinite(position) || !float.IsFinite(particleRadius) || particleRadius < 0.0f)
            return false;

        return Kind switch
        {
            PhysicsChainCpuColliderKind.Sphere => TryCollideSphere(ref position, Center, Radius + particleRadius),
            PhysicsChainCpuColliderKind.Capsule => TryCollideCapsule(ref position, particleRadius),
            PhysicsChainCpuColliderKind.Box => TryCollideBox(ref position, particleRadius),
            PhysicsChainCpuColliderKind.Plane => TryCollidePlane(ref position, particleRadius),
            _ => false,
        };
    }

    private static bool TryCollideSphere(ref Vector3 position, Vector3 center, float radius)
    {
        if (!IsFinite(center) || !float.IsFinite(radius) || radius < 0.0f)
            return false;

        Vector3 delta = position - center;
        float distanceSquared = delta.LengthSquared();
        float radiusSquared = radius * radius;
        if (!float.IsFinite(distanceSquared) || !float.IsFinite(radiusSquared) || distanceSquared >= radiusSquared)
            return false;

        if (distanceSquared <= Epsilon)
            delta = Vector3.UnitY * radius;
        else
            delta *= radius / MathF.Sqrt(distanceSquared);

        position = center + delta;
        return true;
    }

    private bool TryCollideCapsule(ref Vector3 position, float particleRadius)
    {
        if (!IsFinite(Center) || !IsFinite(End) || !float.IsFinite(InverseLengthSquared))
            return false;
        if (InverseLengthSquared <= 0.0f)
            return TryCollideSphere(ref position, Center, Radius + particleRadius);

        Vector3 direction = End - Center;
        float projection = Math.Clamp(Vector3.Dot(position - Center, direction) * InverseLengthSquared, 0.0f, 1.0f);
        Vector3 closest = Center + direction * projection;
        return TryCollideSphere(ref position, closest, Radius + particleRadius);
    }

    private bool TryCollideBox(ref Vector3 position, float particleRadius)
    {
        if (!IsFinite(Center) || !IsFinite(AxisX) || !IsFinite(AxisY) || !IsFinite(AxisZ)
            || !IsFinite(HalfExtents) || HalfExtents.X < 0.0f || HalfExtents.Y < 0.0f || HalfExtents.Z < 0.0f)
            return false;

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
        if (!float.IsFinite(distanceSquared) || distanceSquared > radiusSquared)
            return false;

        if (distanceSquared > Epsilon)
        {
            position = closest + separation * (particleRadius / MathF.Sqrt(distanceSquared));
            return IsFinite(position);
        }

        Vector3 faceDistance = HalfExtents - Vector3.Abs(local);
        if (faceDistance.X <= faceDistance.Y && faceDistance.X <= faceDistance.Z)
            position = Center + AxisX * MathF.CopySign(HalfExtents.X + particleRadius, local.X) + AxisY * local.Y + AxisZ * local.Z;
        else if (faceDistance.Y <= faceDistance.Z)
            position = Center + AxisX * local.X + AxisY * MathF.CopySign(HalfExtents.Y + particleRadius, local.Y) + AxisZ * local.Z;
        else
            position = Center + AxisX * local.X + AxisY * local.Y + AxisZ * MathF.CopySign(HalfExtents.Z + particleRadius, local.Z);
        return IsFinite(position);
    }

    private bool TryCollidePlane(ref Vector3 position, float particleRadius)
    {
        float normalLengthSquared = PlaneNormal.LengthSquared();
        if (!IsFinite(PlaneNormal) || !float.IsFinite(PlaneDistance)
            || !float.IsFinite(normalLengthSquared) || normalLengthSquared <= Epsilon)
            return false;

        float distance = Vector3.Dot(PlaneNormal, position) + PlaneDistance;
        if (!float.IsFinite(distance))
            return false;
        if (Inside == 0u)
        {
            if (distance >= particleRadius)
                return false;
            position += PlaneNormal * (particleRadius - distance);
            return IsFinite(position);
        }

        if (distance <= -particleRadius)
            return false;
        position -= PlaneNormal * (distance + particleRadius);
        return IsFinite(position);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
