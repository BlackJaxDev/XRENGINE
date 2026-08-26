using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Immutable collider shape data. Dynamic world poses are stored separately so
/// ordinary motion never invalidates or reuploads the authored shape stream.
/// </summary>
public readonly record struct PhysicsChainColliderShape(
    PhysicsChainColliderShapeKind Kind,
    Vector3 LocalCenter,
    Vector3 Axis,
    Vector3 HalfExtents,
    float Radius,
    float InverseAxisLengthSquared,
    Vector3 UnitAxis,
    Vector3 LocalBoundsExtents,
    float AxisLengthSquared,
    float AxisLength,
    float RadiusSquared,
    float Diameter,
    float LocalPlaneDistance)
{
    public static PhysicsChainColliderShape Sphere(Vector3 localCenter, float radius)
        => Create(PhysicsChainColliderShapeKind.Sphere, localCenter, Vector3.Zero, Vector3.Zero, radius);

    public static PhysicsChainColliderShape Capsule(Vector3 localCenter, Vector3 axis, float radius)
        => Create(PhysicsChainColliderShapeKind.Capsule, localCenter, axis, Vector3.Zero, radius);

    public static PhysicsChainColliderShape Box(Vector3 localCenter, Vector3 halfExtents)
        => Create(PhysicsChainColliderShapeKind.Box, localCenter, Vector3.Zero, halfExtents, 0.0f);

    public static PhysicsChainColliderShape Plane(Vector3 localCenter, Vector3 normal)
        => Create(PhysicsChainColliderShapeKind.Plane, localCenter, normal, Vector3.Zero, 0.0f);

    private static PhysicsChainColliderShape Create(
        PhysicsChainColliderShapeKind kind,
        Vector3 localCenter,
        Vector3 axis,
        Vector3 halfExtents,
        float radius)
    {
        if (!IsFinite(localCenter) || !IsFinite(axis) || !IsFinite(halfExtents) || !float.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(localCenter), "Collider values must be finite.");
        if (radius < 0.0f || halfExtents.X < 0.0f || halfExtents.Y < 0.0f || halfExtents.Z < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(radius), "Collider dimensions cannot be negative.");

        float axisLengthSquared = axis.LengthSquared();
        if ((kind is PhysicsChainColliderShapeKind.Capsule or PhysicsChainColliderShapeKind.Plane)
            && axisLengthSquared <= 1e-12f)
            throw new ArgumentOutOfRangeException(nameof(axis), "Capsule axes and plane normals must be non-zero.");

        float axisLength = axisLengthSquared > 1e-12f ? MathF.Sqrt(axisLengthSquared) : 0.0f;
        Vector3 unitAxis = axisLength > 0.0f ? axis / axisLength : Vector3.Zero;
        Vector3 localBoundsExtents = kind switch
        {
            PhysicsChainColliderShapeKind.Sphere => new Vector3(radius),
            PhysicsChainColliderShapeKind.Capsule => Vector3.Abs(axis) + new Vector3(radius),
            PhysicsChainColliderShapeKind.Box => halfExtents,
            _ => Vector3.Zero,
        };
        float localPlaneDistance = kind == PhysicsChainColliderShapeKind.Plane
            ? -Vector3.Dot(unitAxis, localCenter)
            : 0.0f;

        return new PhysicsChainColliderShape(
            kind,
            localCenter,
            axis,
            halfExtents,
            radius,
            axisLengthSquared > 1e-12f ? 1.0f / axisLengthSquared : 0.0f,
            unitAxis,
            localBoundsExtents,
            axisLengthSquared,
            axisLength,
            radius * radius,
            radius * 2.0f,
            localPlaneDistance);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
