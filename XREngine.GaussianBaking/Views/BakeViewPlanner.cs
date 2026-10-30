using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Models.Gaussian.Baking.Views;

/// <summary>
/// Plans the camera views that a bake captures. The view directions are points on a Fibonacci sphere,
/// so the same bounds and count always give the same views.
/// </summary>
public static class BakeViewPlanner
{
    /// <summary>The distance factor that keeps a small border around the bounding sphere.</summary>
    private const float FramingMargin = 1.1f;

    /// <summary>The cosine of 10 degrees. A view direction nearer to the Y axis than this uses +Z as up.</summary>
    private static readonly float PoleCosine = MathF.Cos(10.0f * MathF.PI / 180.0f);

    /// <summary>The golden angle, pi × (3 − √5), in radians.</summary>
    private static readonly float GoldenAngle = MathF.PI * (3.0f - MathF.Sqrt(5.0f));

    /// <summary>
    /// Plans views on a sphere around the bounds. Each camera looks at the center of the bounds, at a distance
    /// where the bounding sphere fits the vertical field of view with a 10 percent margin.
    /// </summary>
    /// <param name="bounds">The world bounds of the content.</param>
    /// <param name="count">The number of views.</param>
    /// <param name="verticalFovDegrees">The vertical field of view of each camera, in degrees.</param>
    public static IReadOnlyList<BakeCameraPose> PlanOrbitViews(AABB bounds, int count = 32, float verticalFovDegrees = 40f)
    {
        Validate(bounds, count, verticalFovDegrees);

        Vector3 center = bounds.Center;
        float radius = bounds.Size.Length() * 0.5f;
        float halfFovRadians = verticalFovDegrees * 0.5f * MathF.PI / 180.0f;
        float distance = radius / MathF.Sin(halfFovRadians) * FramingMargin;

        BakeCameraPose[] views = new BakeCameraPose[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 direction = FibonacciDirection(i, count);
            views[i] = CreatePose(center + direction * distance, -direction, verticalFovDegrees);
        }

        return views;
    }

    /// <summary>
    /// Plans views from the center of the bounds. Each camera looks outward along one Fibonacci direction.
    /// </summary>
    /// <param name="bounds">The world bounds of the content.</param>
    /// <param name="count">The number of views.</param>
    /// <param name="verticalFovDegrees">The vertical field of view of each camera, in degrees.</param>
    public static IReadOnlyList<BakeCameraPose> PlanInteriorViews(AABB bounds, int count = 32, float verticalFovDegrees = 75f)
    {
        Validate(bounds, count, verticalFovDegrees);

        Vector3 center = bounds.Center;
        BakeCameraPose[] views = new BakeCameraPose[count];
        for (int i = 0; i < count; i++)
            views[i] = CreatePose(center, FibonacciDirection(i, count), verticalFovDegrees);

        return views;
    }

    /// <summary>
    /// Returns the unit direction of point <paramref name="index"/> of <paramref name="count"/> on a Fibonacci sphere.
    /// Y goes from near +1 to near −1 in equal steps, and the azimuth turns by the golden angle at each step.
    /// </summary>
    private static Vector3 FibonacciDirection(int index, int count)
    {
        float y = 1.0f - (2.0f * index + 1.0f) / count;
        float ringRadius = MathF.Sqrt(MathF.Max(0.0f, 1.0f - y * y));
        float azimuth = GoldenAngle * index;
        return Vector3.Normalize(new Vector3(MathF.Cos(azimuth) * ringRadius, y, MathF.Sin(azimuth) * ringRadius));
    }

    private static BakeCameraPose CreatePose(Vector3 position, Vector3 forward, float verticalFovDegrees)
    {
        Vector3 up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) > PoleCosine
            ? Vector3.UnitZ
            : Vector3.UnitY;

        // CreateWorld puts -forward on the Z axis, so the camera looks along its local -Z axis.
        Matrix4x4 orientation = Matrix4x4.CreateWorld(Vector3.Zero, forward, up);
        Quaternion rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(orientation));
        return new BakeCameraPose(position, rotation, verticalFovDegrees);
    }

    private static void Validate(AABB bounds, int count, float verticalFovDegrees)
    {
        if (!bounds.IsValid)
            throw new ArgumentException("The bounds must be finite, and the minimum must not be larger than the maximum.", nameof(bounds));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (!float.IsFinite(verticalFovDegrees) || verticalFovDegrees <= 0.0f || verticalFovDegrees >= 180.0f)
            throw new ArgumentOutOfRangeException(nameof(verticalFovDegrees), verticalFovDegrees, "The field of view must be between 0 and 180 degrees.");
    }
}
