using System.Numerics;
using JoltPhysicsSharp;
using XREngine.Components.Physics;
using XREngine.Data.Tools;

namespace XREngine.Scene.Physics.Jolt;

/// <summary>
/// Validates authored CoACD convex hulls against Jolt and attaches them to a
/// <see cref="StaticRigidBodyComponent"/> on the physics thread.
/// </summary>
internal static class JoltConvexHullInstaller
{
    private const float MinimumScaleMagnitude = 1.0e-5f;
    private const float UnitScaleToleranceSquared = 1.0e-6f;

    /// <summary>
    /// Converts valid convex hulls into backend-neutral <see cref="PhysicsColliderShape"/> entries
    /// and queues static rigid-body reconstruction on the physics thread.
    /// </summary>
    public static void PrepareAndAttach(
        StaticRigidBodyComponent component,
        IReadOnlyList<CoACD.ConvexHullMesh> hulls)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(hulls);

        if (hulls.Count == 0)
            return;

        Vector3 shapeScale = ResolvePositiveScale(component.Transform.WorldMatrix);
        Vector3 shapeOffsetTranslation = component.ShapeOffsetTranslation;
        Quaternion shapeOffsetRotation = component.ShapeOffsetRotation;

        List<PhysicsColliderShape> shapes = new(hulls.Count);
        int skippedCount = 0;

        for (int hullIndex = 0; hullIndex < hulls.Count; hullIndex++)
        {
            CoACD.ConvexHullMesh? hull = hulls[hullIndex];
            if (!TryCreateValidatedShape(
                    hull,
                    shapeScale,
                    shapeOffsetTranslation,
                    shapeOffsetRotation,
                    shapes.Count + 1,
                    out PhysicsColliderShape? shape))
            {
                skippedCount++;
                continue;
            }

            shapes.Add(shape!);
        }

        if (skippedCount > 0)
            Debug.Physics(
                $"[JoltConvexHullInstaller] Skipped {skippedCount} degenerate convex hull(s) while preparing '{component.SceneNode?.Name ?? "<unnamed>"}'.");

        if (shapes.Count == 0)
            return;

        RuntimeThreadServices.Current.EnqueuePhysicsThread(() => AttachShapes(component, shapes));
    }

    private static bool TryCreateValidatedShape(
        CoACD.ConvexHullMesh? hull,
        Vector3 shapeScale,
        Vector3 localPosition,
        Quaternion localRotation,
        int displayIndex,
        out PhysicsColliderShape? shape)
    {
        shape = null;
        if (hull?.Vertices is not { Length: >= 4 } sourceVertices)
            return false;

        for (int i = 0; i < sourceVertices.Length; i++)
        {
            Vector3 vertex = sourceVertices[i];
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z))
                return false;
        }

        // Validate each hull independently so one flat or zero-volume CoACD sliver does not abort
        // creation of the surrounding static compound shape in Jolt.
        try
        {
            using Shape testShape = JoltShapeFactory.CreateConvexHull(sourceVertices, shapeScale, Quaternion.Identity);
            if (!float.IsFinite(testShape.Volume) || testShape.Volume <= 0.0f)
                return false;
        }
        catch
        {
            return false;
        }

        Vector3[] verticesCopy = (Vector3[])sourceVertices.Clone();
        uint[] indicesCopy = CopyValidTriangleIndices(hull.Indices, verticesCopy.Length);

        shape = new PhysicsColliderShape
        {
            Name = $"Hull {displayIndex}",
            LocalPosition = localPosition,
            LocalRotation = localRotation,
            Geometry = new PhysicsConvexHullGeometry(verticesCopy, indicesCopy)
            {
                Scale = shapeScale,
            },
        };
        return true;
    }

    private static uint[] CopyValidTriangleIndices(int[]? sourceIndices, int vertexCount)
    {
        if (sourceIndices is null || sourceIndices.Length == 0 || sourceIndices.Length % 3 != 0)
            return [];

        uint[] result = new uint[sourceIndices.Length];
        for (int i = 0; i < sourceIndices.Length; i++)
        {
            int index = sourceIndices[i];
            if (index < 0 || index >= vertexCount)
                return [];
            result[i] = (uint)index;
        }

        return result;
    }

    private static Vector3 ResolvePositiveScale(Matrix4x4 worldMatrix)
    {
        if (!Matrix4x4.Decompose(worldMatrix, out Vector3 scale, out _, out _))
            return Vector3.One;

        Vector3 absScale = new(MathF.Abs(scale.X), MathF.Abs(scale.Y), MathF.Abs(scale.Z));
        if (!float.IsFinite(absScale.X) || !float.IsFinite(absScale.Y) || !float.IsFinite(absScale.Z)
            || absScale.X < MinimumScaleMagnitude
            || absScale.Y < MinimumScaleMagnitude
            || absScale.Z < MinimumScaleMagnitude)
        {
            return Vector3.One;
        }

        return Vector3.DistanceSquared(absScale, Vector3.One) <= UnitScaleToleranceSquared
            ? Vector3.One
            : absScale;
    }

    private static void AttachShapes(
        StaticRigidBodyComponent component,
        List<PhysicsColliderShape> shapes)
    {
        if (!component.IsActive
            || component.RigidBody is not null
            || component.ColliderShapes.Count > 0
            || component.Geometry is not null)
        {
            return;
        }

        component.ColliderShapes = shapes;
        component.RebuildCollisionShapes(wakeOnLostTouch: false);
    }
}
