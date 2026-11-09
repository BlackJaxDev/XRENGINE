using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private const float MinimumLimitDirectionLength = 1e-6f;

    /// <summary>
    /// Keeps a child particle inside the configured limit around the rest direction of its segment.
    /// </summary>
    /// <remarks>
    /// Each limit is relative to its parent segment, so a chain can curl further than one limit in total.
    /// The rest direction follows the simulated turn of the parent segment.
    /// </remarks>
    private void ApplyAngleLimit(List<Particle> particles, Particle child, Particle parent, Vector3 restPosition)
    {
        if (LimitType == EPhysicsChainLimitType.None)
            return;

        Vector3 restOffset = restPosition - parent.Position;
        Vector3 offset = child.Position - parent.Position;
        float restLength = restOffset.Length();
        float length = offset.Length();
        if (restLength <= MinimumLimitDirectionLength || length <= MinimumLimitDirectionLength)
            return;

        Vector3 axis = restOffset / restLength;
        Vector3 direction = offset / length;
        Quaternion parentTurn = Quaternion.Identity;
        if (parent.ParentIndex >= 0)
        {
            Particle grandparent = particles[parent.ParentIndex];
            Vector3 parentRest = parent.TransformPosition - grandparent.TransformPosition;
            Vector3 parentSimulated = parent.Position - grandparent.Position;
            if (parentRest.LengthSquared() > MinimumSegmentDirectionLengthSquared &&
                parentSimulated.LengthSquared() > MinimumSegmentDirectionLengthSquared)
            {
                parentTurn = Quaternion.Normalize(XRMath.RotationBetweenVectors(parentRest, parentSimulated));
                axis = Vector3.Normalize(Vector3.Transform(axis, parentTurn));
            }
        }

        // The limit rotation is authored in the local space of the parent bone.
        Matrix4x4 parentWorld = parent.TransformLocalToWorldMatrix;
        Vector3 side = Vector3.TransformNormal(Vector3.UnitX, parentWorld);
        side = Vector3.Transform(side, parentTurn);
        Quaternion limitRotation = Quaternion.Normalize(LimitRotation);
        if (limitRotation != Quaternion.Identity &&
            Matrix4x4.Decompose(parentWorld, out _, out Quaternion parentRotation, out _))
        {
            Quaternion worldLimit = Quaternion.Normalize(parentTurn * parentRotation * limitRotation * Quaternion.Inverse(parentTurn * parentRotation));
            axis = Vector3.Normalize(Vector3.Transform(axis, worldLimit));
            side = Vector3.Transform(side, worldLimit);
        }

        // The side axis and the up axis complete an orthonormal frame around the limit axis.
        side -= axis * Vector3.Dot(side, axis);
        if (side.LengthSquared() <= MinimumLimitDirectionLength)
            side = MathF.Abs(axis.Y) < 0.9f ? Vector3.Cross(Vector3.UnitY, axis) : Vector3.Cross(Vector3.UnitX, axis);
        side = Vector3.Normalize(side);
        Vector3 up = Vector3.Cross(axis, side);

        Vector3 limited = LimitType switch
        {
            EPhysicsChainLimitType.Hinge => LimitToHinge(direction, axis, up, child.MaxAngleRadians),
            EPhysicsChainLimitType.Polar => LimitToPolar(direction, axis, side, up, child.MaxAngleRadians, child.MaxYawRadians),
            _ => LimitToCone(direction, axis, side, child.MaxAngleRadians),
        };
        child.Position = parent.Position + limited * length;
    }

    private static Vector3 LimitToCone(Vector3 direction, Vector3 axis, Vector3 fallbackSide, float maximumRadians)
    {
        float cosine = Math.Clamp(Vector3.Dot(direction, axis), -1.0f, 1.0f);
        if (MathF.Acos(cosine) <= maximumRadians)
            return direction;

        Vector3 tangent = direction - axis * cosine;
        tangent = tangent.LengthSquared() > MinimumLimitDirectionLength ? Vector3.Normalize(tangent) : fallbackSide;
        return Vector3.Normalize(axis * MathF.Cos(maximumRadians) + tangent * MathF.Sin(maximumRadians));
    }

    private static Vector3 LimitToHinge(Vector3 direction, Vector3 axis, Vector3 planeTangent, float maximumRadians)
    {
        // The segment turns around the side axis, so it moves in the axis and up plane.
        float angle = MathF.Atan2(Vector3.Dot(direction, planeTangent), Vector3.Dot(direction, axis));
        angle = Math.Clamp(angle, -maximumRadians, maximumRadians);
        return Vector3.Normalize(axis * MathF.Cos(angle) + planeTangent * MathF.Sin(angle));
    }

    private static Vector3 LimitToPolar(
        Vector3 direction,
        Vector3 axis,
        Vector3 side,
        Vector3 up,
        float maximumPitchRadians,
        float maximumYawRadians)
    {
        float forward = Vector3.Dot(direction, axis);
        float pitch = MathF.Atan2(Vector3.Dot(direction, up), forward);
        float yaw = MathF.Atan2(Vector3.Dot(direction, side), forward);
        float pitchRatio = maximumPitchRadians > 0.0f ? pitch / maximumPitchRadians : (pitch == 0.0f ? 0.0f : float.PositiveInfinity);
        float yawRatio = maximumYawRadians > 0.0f ? yaw / maximumYawRadians : (yaw == 0.0f ? 0.0f : float.PositiveInfinity);
        float excess = pitchRatio * pitchRatio + yawRatio * yawRatio;
        if (excess <= 1.0f)
            return direction;

        // An ellipse of pitch and yaw forms the segment of the sphere.
        if (float.IsInfinity(excess))
        {
            pitch = Math.Clamp(pitch, -maximumPitchRadians, maximumPitchRadians);
            yaw = Math.Clamp(yaw, -maximumYawRadians, maximumYawRadians);
        }
        else
        {
            float scale = 1.0f / MathF.Sqrt(excess);
            pitch *= scale;
            yaw *= scale;
        }

        return Vector3.Normalize(axis + up * MathF.Tan(pitch) + side * MathF.Tan(yaw));
    }

    /// <summary>
    /// Sets the segment length. Motion can change the length only through <see cref="StretchMotion"/>.
    /// A collision can use the full range.
    /// </summary>
    private void ApplySegmentLength(Particle child, Particle parent, float restLength)
    {
        Vector3 offset = parent.Position - child.Position;
        float length = offset.Length();
        if (length <= 0.0f)
            return;

        float target = restLength;
        if (MaxStretch > 0.0f || MaxSquish > 0.0f)
        {
            float allowed = Math.Clamp(length, restLength * (1.0f - MaxSquish), restLength * (1.0f + MaxStretch));
            target = child.IsColliding ? allowed : Interp.Lerp(restLength, allowed, StretchMotion);
        }

        child.Position += offset * ((length - target) / length);
    }

    /// <summary>
    /// Keeps a child of a branching bone at its rest offset when <see cref="PinBranchChildren"/> is set.
    /// </summary>
    /// <returns>True when the child is pinned. The caller then skips the other constraints.</returns>
    private bool TryPinBranchChild(List<Particle> particles, Particle child, Particle parent)
    {
        if (!IsPinnedBranchChild(child, parent))
            return false;

        if (RestFollowsParent)
        {
            child.Position = parent.Position + ComputeShapeRestOffset(child, parent);
            child.SegmentTurn = parent.SegmentTurn;
            return true;
        }

        // A branching bone does not rotate by itself, but its own parent segment can turn.
        Vector3 restOffset = Vector3.TransformNormal(child.TransformLocalPosition, parent.TransformLocalToWorldMatrix);
        if (parent.ParentIndex >= 0)
        {
            Particle grandparent = particles[parent.ParentIndex];
            Vector3 parentRest = parent.TransformPosition - grandparent.TransformPosition;
            Vector3 parentSimulated = parent.Position - grandparent.Position;
            if (parentRest.LengthSquared() > MinimumSegmentDirectionLengthSquared &&
                parentSimulated.LengthSquared() > MinimumSegmentDirectionLengthSquared)
                restOffset = Vector3.Transform(restOffset, Quaternion.Normalize(XRMath.RotationBetweenVectors(parentRest, parentSimulated)));
        }

        child.Position = parent.Position + restOffset;
        return true;
    }

    private bool IsPinnedBranchChild(Particle child, Particle parent)
        => PinBranchChildren && parent.ChildCount > 1 && child.Transform is not null;
}
