using System.Numerics;

namespace MonkeyBallVR;

/// <summary>
/// Asset-authored circular bumper used by the deterministic MonkeyBall simulation.
/// </summary>
public sealed class MonkeyBallBumper
{
    public MonkeyBallBumper()
    {
    }

    public MonkeyBallBumper(Vector2 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    public Vector2 Center { get; set; }

    public float Radius { get; set; }
}
