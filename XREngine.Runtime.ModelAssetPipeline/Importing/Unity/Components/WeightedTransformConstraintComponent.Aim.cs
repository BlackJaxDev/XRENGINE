using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public sealed partial class WeightedTransformConstraintComponent
{
    private const float AimEpsilonSquared = 1e-12f;

    /// <summary>
    /// Computes the rotation, in the solve space, that turns <see cref="AimAxis"/> toward the weighted
    /// source position and turns <see cref="UpAxis"/> toward the world-up direction, then applies
    /// <see cref="Roll"/> around the aim axis. The constraint-level rotation offset is not applied here.
    /// Returns false when the source position and the target position are the same point.
    /// </summary>
    private bool TrySolveAim(Transform target, Vector3 sourcePosition, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        Vector3 targetPosition = SolveInLocalSpace ? target.Translation : target.WorldTranslation;
        Vector3 direction = sourcePosition - targetPosition;
        if (direction.LengthSquared() < AimEpsilonSquared)
            return false;

        direction = Vector3.Normalize(direction);
        Vector3 aimAxis = NormalizeOrFallback(AimAxis, -Vector3.UnitZ);
        rotation = TryGetWorldUp(targetPosition, out Vector3 worldUp)
            ? CreateAimRotation(aimAxis, UpAxis, direction, worldUp)
            : CreateFromToRotation(aimAxis, direction);

        if (Roll != 0.0f)
        {
            const float degreesToRadians = MathF.PI / 180.0f;
            rotation = Quaternion.Normalize(rotation * Quaternion.CreateFromAxisAngle(aimAxis, Roll * degreesToRadians));
        }

        return true;
    }

    /// <summary>
    /// Reads the world-up direction in the solve space for <see cref="WorldUpType"/>. Returns false for
    /// <see cref="TransformConstraintWorldUpType.None"/> or when the direction has no length.
    /// </summary>
    private bool TryGetWorldUp(Vector3 targetPosition, out Vector3 worldUp)
    {
        TransformBase? upTransform = WorldUpTransform;
        worldUp = WorldUpType switch
        {
            TransformConstraintWorldUpType.SceneUp => Vector3.UnitY,
            TransformConstraintWorldUpType.ObjectUp => upTransform is null
                ? Vector3.UnitY
                : (SolveInLocalSpace ? GetLocalTranslation(upTransform) : upTransform.WorldTranslation) - targetPosition,
            TransformConstraintWorldUpType.ObjectRotationUp => upTransform is null
                ? WorldUpVector
                : Vector3.Transform(WorldUpVector, SolveInLocalSpace ? GetLocalRotation(upTransform) : upTransform.WorldRotation),
            TransformConstraintWorldUpType.Vector => WorldUpVector,
            _ => Vector3.Zero,
        };

        if (worldUp.LengthSquared() < AimEpsilonSquared)
            return false;

        worldUp = Vector3.Normalize(worldUp);
        return true;
    }

    /// <summary>
    /// Builds the rotation that maps the local aim axis onto <paramref name="aimDirection"/> and turns the
    /// local up axis as close to <paramref name="upDirection"/> as the aim allows. Both pairs are made
    /// orthonormal first (the up vectors lose their part along the aim). When the up direction is
    /// parallel to the aim direction, the result is the shortest rotation of the aim axis.
    /// </summary>
    internal static Quaternion CreateAimRotation(Vector3 localAim, Vector3 localUp, Vector3 aimDirection, Vector3 upDirection)
    {
        Vector3 a = NormalizeOrFallback(localAim, -Vector3.UnitZ);
        Vector3 aim = NormalizeOrFallback(aimDirection, a);
        Vector3 worldUp = upDirection - (aim * Vector3.Dot(upDirection, aim));
        if (worldUp.LengthSquared() < AimEpsilonSquared)
            return CreateFromToRotation(a, aim);

        Vector3 u = localUp - (a * Vector3.Dot(localUp, a));
        if (u.LengthSquared() < AimEpsilonSquared)
            u = AnyPerpendicular(a);
        u = Vector3.Normalize(u);
        worldUp = Vector3.Normalize(worldUp);
        Vector3 c = Vector3.Cross(a, u);
        Vector3 worldC = Vector3.Cross(aim, worldUp);

        // System.Numerics uses row vectors (v * M). With the local frame as the rows of L and the target
        // frame as the rows of W, transpose(L) * W maps a to aim, u to worldUp, and c to worldC.
        Matrix4x4 local = new(
            a.X, a.Y, a.Z, 0.0f,
            u.X, u.Y, u.Z, 0.0f,
            c.X, c.Y, c.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
        Matrix4x4 world = new(
            aim.X, aim.Y, aim.Z, 0.0f,
            worldUp.X, worldUp.Y, worldUp.Z, 0.0f,
            worldC.X, worldC.Y, worldC.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(local) * world));
    }

    /// <summary>The shortest rotation that maps <paramref name="from"/> onto <paramref name="to"/>.</summary>
    internal static Quaternion CreateFromToRotation(Vector3 from, Vector3 to)
    {
        from = NormalizeOrFallback(from, -Vector3.UnitZ);
        to = NormalizeOrFallback(to, from);
        float dot = Vector3.Dot(from, to);
        if (dot < -0.999999f)
            return Quaternion.CreateFromAxisAngle(AnyPerpendicular(from), MathF.PI);

        Vector3 axis = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(axis, 1.0f + dot));
    }

    private static Vector3 AnyPerpendicular(Vector3 value)
    {
        Vector3 other = MathF.Abs(value.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(value, other));
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
        => value.LengthSquared() < AimEpsilonSquared ? fallback : Vector3.Normalize(value);
}
