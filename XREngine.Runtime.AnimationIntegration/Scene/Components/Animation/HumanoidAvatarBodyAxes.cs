using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Canonical avatar-body axes in the imported model-root coordinate system.
/// </summary>
public class HumanoidAvatarBodyAxes
{
    public Vector3 Right { get; set; } = -Vector3.UnitX;
    public Vector3 Up { get; set; } = Vector3.UnitY;
    public Vector3 Forward { get; set; } = Vector3.UnitZ;

    public bool IsFiniteOrthonormal(float tolerance = 1e-3f)
    {
        if (!IsFiniteUnit(Right, tolerance)
            || !IsFiniteUnit(Up, tolerance)
            || !IsFiniteUnit(Forward, tolerance))
            return false;

        return MathF.Abs(Vector3.Dot(Right, Up)) <= tolerance
            && MathF.Abs(Vector3.Dot(Right, Forward)) <= tolerance
            && MathF.Abs(Vector3.Dot(Up, Forward)) <= tolerance;
    }

    private static bool IsFiniteUnit(Vector3 value, float tolerance)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && MathF.Abs(value.LengthSquared() - 1.0f) <= tolerance;
}
