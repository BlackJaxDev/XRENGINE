using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>Allocation-free body-follow math, independent of headset orientation and render ownership.</summary>
public sealed class VrSpectatorFollowState
{
    private bool _initialized;
    private Vector3 _position;
    private Quaternion _yaw = Quaternion.Identity;
    public void Reset() => _initialized = false;

    public VrSpectatorFollowPose Evaluate(in Matrix4x4 playerRoot, Matrix4x4? hips,
        VrSpectatorFollowSettings settings, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Vector3 root = playerRoot.Translation;
        if (!Finite(root))
            throw new ArgumentException("Player-root position must be finite.", nameof(playerRoot));
        Vector3 forward = HorizontalForward(hips ?? playerRoot);
        if (forward.LengthSquared() < 1e-8f)
            forward = HorizontalForward(playerRoot);
        if (forward.LengthSquared() < 1e-8f)
            forward = Vector3.Transform(-Vector3.UnitZ, _yaw);
        forward = Vector3.Normalize(forward);
        Quaternion yaw = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, Vector3.UnitY));
        Vector3 position = root;
        if (hips is Matrix4x4 body && float.IsFinite(body.M42))
            position.Y = body.M42;
        float alpha = !_initialized || settings.Smoothing <= 0f ? 1f :
            1f - MathF.Exp(-settings.Smoothing * Math.Max(0f, float.IsFinite(deltaSeconds) ? deltaSeconds : 0f));
        _position = Vector3.Lerp(_initialized ? _position : position, position, alpha);
        _yaw = Quaternion.Normalize(Quaternion.Slerp(_yaw, yaw, alpha));
        _initialized = true;
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(_yaw);
        Vector3 shoulder = Vector3.TransformNormal(new(settings.ShoulderOffset, settings.Height, 0), rotation);
        Matrix4x4 origin = rotation;
        origin.Translation = _position + shoulder;
        Vector3 aim = _position + Vector3.TransformNormal(settings.AimOffset, rotation);
        Vector3 trailing = origin.Translation + Vector3.TransformNormal(new(0, 0, settings.Distance), rotation);
        return new(origin, aim, trailing);
    }

    public static Quaternion LookAt(Vector3 position, Vector3 target, Quaternion fallback)
    {
        Vector3 direction = target - position;
        if (!Finite(direction) || direction.LengthSquared() < 1e-8f)
            return fallback;
        direction = Vector3.Normalize(direction);
        Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > .999f ? Vector3.UnitZ : Vector3.UnitY;
        return Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, direction, up));
    }

    /// <summary>Applies a world pose using local * parent row-vector composition, including a moving parent.</summary>
    public static void ApplyWorldPose(Transform transform, in Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(transform);
        transform.DeriveLocalMatrix(world * transform.ParentInverseWorldMatrix);
        transform.RecalculateMatrices(forceWorldRecalc: true);
    }

    private static Vector3 HorizontalForward(in Matrix4x4 matrix)
    {
        Vector3 forward = new(-matrix.M31, 0, -matrix.M33);
        return Finite(forward) ? forward : Vector3.Zero;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
