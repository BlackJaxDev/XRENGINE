using System.Numerics;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    private readonly Dictionary<List<PhysicsChainColliderBase>, PhysicsChainCpuSharedColliderSet> _cpuSharedColliderSets =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    internal bool TryGetOrCreateCpuSharedColliderSet(
        List<PhysicsChainColliderBase>? resourceKey,
        ReadOnlySpan<PhysicsChainColliderSnapshot> snapshots,
        out PhysicsChainCpuSharedColliderSet? sharedSet)
    {
        sharedSet = null;
        if (resourceKey is null || resourceKey.Count != snapshots.Length)
            return false;

        var shapes = new PhysicsChainColliderShape[snapshots.Length];
        var poses = new Matrix4x4[snapshots.Length];
        for (int colliderIndex = 0; colliderIndex < snapshots.Length; ++colliderIndex)
        {
            if (!TryConvertCollider(snapshots[colliderIndex], out shapes[colliderIndex], out poses[colliderIndex]))
                return false;
        }

        if (!_cpuSharedColliderSets.TryGetValue(resourceKey, out sharedSet)
            || !sharedSet.RuntimeSet.ColliderSet.ContentEquals(shapes))
        {
            PhysicsChainColliderSet colliderSet = PhysicsChainWorldColliderSetExtensions.GetOrCreateColliderSet(this, shapes);
            sharedSet = new PhysicsChainCpuSharedColliderSet(colliderSet);
            _cpuSharedColliderSets[resourceKey] = sharedSet;
        }

        for (int colliderIndex = 0; colliderIndex < poses.Length; ++colliderIndex)
            if (!sharedSet.TrySetPose(colliderIndex, poses[colliderIndex]))
                return false;
        return true;
    }

    private void SynchronizeCpuSharedColliderSets()
    {
        foreach (PhysicsChainCpuSharedColliderSet sharedSet in _cpuSharedColliderSets.Values)
            if (!sharedSet.RuntimeSet.TrySynchronizeDirtyPoses())
                throw new InvalidOperationException(
                    $"Physics chain collider set {sharedSet.RuntimeSet.ColliderSet.StableId} could not refit its dirty pose range.");
    }

    internal static bool CpuSharedColliderShapesMatch(
        PhysicsChainCpuSharedColliderSet sharedSet,
        PhysicsChainColliderSnapshot[]? snapshots,
        int snapshotCount)
    {
        if (snapshotCount != sharedSet.ColliderCount
            || (snapshotCount > 0 && snapshots is null))
            return false;

        ReadOnlySpan<PhysicsChainColliderShape> shapes = sharedSet.RuntimeSet.ColliderSet.Shapes.Span;
        for (int colliderIndex = 0; colliderIndex < snapshotCount; ++colliderIndex)
        {
            if (!TryConvertCollider(snapshots![colliderIndex], out PhysicsChainColliderShape shape, out _)
                || shape != shapes[colliderIndex])
                return false;
        }
        return true;
    }

    internal static bool TryUpdateCpuSharedColliderPoses(
        PhysicsChainCpuSharedColliderSet sharedSet,
        ReadOnlySpan<PhysicsChainColliderSnapshot> snapshots)
    {
        if (snapshots.Length != sharedSet.ColliderCount)
            return false;
        for (int colliderIndex = 0; colliderIndex < snapshots.Length; ++colliderIndex)
        {
            if (!TryConvertCollider(snapshots[colliderIndex], out _, out Matrix4x4 pose)
                || !sharedSet.TrySetPose(colliderIndex, pose))
                return false;
        }
        return true;
    }

    private static bool TryConvertCollider(
        in PhysicsChainColliderSnapshot snapshot,
        out PhysicsChainColliderShape shape,
        out Matrix4x4 pose)
    {
        switch (snapshot.Kind)
        {
            case PhysicsChainColliderKind.Sphere:
                shape = PhysicsChainColliderShape.Sphere(Vector3.Zero, snapshot.Radius);
                pose = Matrix4x4.CreateTranslation(snapshot.Center);
                return true;
            case PhysicsChainColliderKind.Capsule:
            {
                Vector3 halfAxis = (snapshot.End - snapshot.Center) * 0.5f;
                float halfLength = halfAxis.Length();
                if (!float.IsFinite(halfLength) || halfLength <= 1e-6f)
                    break;
                Vector3 center = (snapshot.Center + snapshot.End) * 0.5f;
                shape = PhysicsChainColliderShape.Capsule(Vector3.Zero, Vector3.UnitY * halfLength, snapshot.Radius);
                pose = CreatePose(center, halfAxis / halfLength);
                return true;
            }
            case PhysicsChainColliderKind.Box:
                shape = PhysicsChainColliderShape.Box(Vector3.Zero, snapshot.HalfExtents);
                pose = new Matrix4x4(
                    snapshot.AxisX.X, snapshot.AxisX.Y, snapshot.AxisX.Z, 0.0f,
                    snapshot.AxisY.X, snapshot.AxisY.Y, snapshot.AxisY.Z, 0.0f,
                    snapshot.AxisZ.X, snapshot.AxisZ.Y, snapshot.AxisZ.Z, 0.0f,
                    snapshot.Center.X, snapshot.Center.Y, snapshot.Center.Z, 1.0f);
                return true;
            case PhysicsChainColliderKind.Plane when !snapshot.Inside:
            {
                Vector3 normal = Vector3.Normalize(snapshot.PlaneNormal);
                if (!IsFinite(normal))
                    break;
                shape = PhysicsChainColliderShape.Plane(Vector3.Zero, Vector3.UnitY);
                pose = CreatePose(-snapshot.PlaneDistance * normal, normal);
                return true;
            }
        }

        shape = default;
        pose = default;
        return false;
    }

    private static Matrix4x4 CreatePose(Vector3 center, Vector3 axisY)
    {
        Vector3 reference = MathF.Abs(axisY.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 axisX = Vector3.Normalize(Vector3.Cross(reference, axisY));
        Vector3 axisZ = Vector3.Normalize(Vector3.Cross(axisY, axisX));
        return new Matrix4x4(
            axisX.X, axisX.Y, axisX.Z, 0.0f,
            axisY.X, axisY.Y, axisY.Z, 0.0f,
            axisZ.X, axisZ.Y, axisZ.Z, 0.0f,
            center.X, center.Y, center.Z, 1.0f);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
