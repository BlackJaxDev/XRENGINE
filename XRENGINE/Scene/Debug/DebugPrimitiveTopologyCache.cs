using System.Numerics;

namespace XREngine.Rendering.Debugging;

/// <summary>
/// Process-lifetime unit topology shared by transient debug shape submissions.
/// Trigonometry is paid once instead of once per component and frame.
/// </summary>
internal static class DebugPrimitiveTopologyCache
{
    public const int CircleSegments = 16;
    public const int SphereSegments = 12;
    public const int SphereRings = 8;

    public static Vector2[] UnitCircle { get; } = CreateUnitCircle();
    public static Vector3[] UnitSphere { get; } = CreateUnitSphere();

    private static Vector2[] CreateUnitCircle()
    {
        Vector2[] points = new Vector2[CircleSegments + 1];
        for (int index = 0; index <= CircleSegments; index++)
        {
            float angle = MathF.Tau * index / CircleSegments;
            points[index] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        return points;
    }

    private static Vector3[] CreateUnitSphere()
    {
        Vector3[] points = new Vector3[SphereSegments * SphereRings];
        for (int ring = 0; ring < SphereRings; ring++)
        {
            float theta = MathF.PI * ring / (SphereRings - 1);
            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);
            for (int segment = 0; segment < SphereSegments; segment++)
            {
                float phi = MathF.Tau * segment / SphereSegments;
                points[ring * SphereSegments + segment] = new Vector3(
                    MathF.Cos(phi) * sinTheta,
                    cosTheta,
                    MathF.Sin(phi) * sinTheta);
            }
        }
        return points;
    }
}
