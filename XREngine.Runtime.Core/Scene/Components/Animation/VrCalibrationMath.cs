using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>Rigid capture math in the engine's row-vector, meter-based world convention.</summary>
public static class VrCalibrationMath
{
    public static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    public static bool TryGetRigidPose(Matrix4x4 matrix, out Matrix4x4 rigid)
    {
        rigid = default;
        if (!IsFinite(matrix) || !Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
            || MathF.Abs(scale.X * scale.Y * scale.Z) < 1e-8f || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-8f)
            return false;
        rigid = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(translation);
        return IsFinite(rigid);
    }

    public static bool TryCaptureOffset(Matrix4x4 targetWorld, Matrix4x4 deviceWorld, out Matrix4x4 offset)
    {
        offset = default;
        if (!TryGetRigidPose(targetWorld, out Matrix4x4 target) || !IsFinite(deviceWorld)
            || !Matrix4x4.Invert(deviceWorld, out Matrix4x4 inverse))
            return false;
        offset = target * inverse;
        return IsFinite(offset) && Matrix4x4.Decompose(offset, out _, out Quaternion rotation, out _)
            && float.IsFinite(rotation.LengthSquared()) && rotation.LengthSquared() > 1e-8f;
    }

    /// <summary>Yaw has no effect on this level-head test.</summary>
    public static bool IsHeadLevel(Matrix4x4 headWorld, float toleranceDegrees)
    {
        if (!TryGetRigidPose(headWorld, out Matrix4x4 head) || !float.IsFinite(toleranceDegrees)
            || toleranceDegrees < 0 || toleranceDegrees > 90)
            return false;
        Vector3 up = Vector3.TransformNormal(Vector3.UnitY, head);
        return Vector3.Dot(up, Vector3.UnitY) >= MathF.Cos(float.DegreesToRadians(toleranceDegrees));
    }
}
