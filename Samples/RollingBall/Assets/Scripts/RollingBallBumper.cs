using System.Numerics;

namespace RollingBall;

/// <summary>
/// Asset-authored circular bumper used by the deterministic Rolling Ball simulation.
/// </summary>
public sealed class RollingBallBumper
{
    public RollingBallBumper()
    {
    }

    public RollingBallBumper(Vector2 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    public Vector2 Center { get; set; }

    public float Radius { get; set; }
}
