using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// One ground-projected future root sample produced by the MotionBricks locomotion planner
/// after querying terrain elevation and obstacle collisions.
/// </summary>
public readonly struct MotionBricksTrajectoryPoint
{
    public MotionBricksTrajectoryPoint(
        Vector3 position,
        Vector3 groundNormal,
        float facingYawRadians,
        float travelYawRadians,
        float speedMetersPerSecond,
        float timeSeconds,
        bool isCollisionClamped,
        bool hasGroundHit)
    {
        Position = position;
        GroundNormal = groundNormal;
        FacingYawRadians = facingYawRadians;
        TravelYawRadians = travelYawRadians;
        SpeedMetersPerSecond = speedMetersPerSecond;
        TimeSeconds = timeSeconds;
        IsCollisionClamped = isCollisionClamped;
        HasGroundHit = hasGroundHit;
    }

    /// <summary>World-space ground point in meters along the projected future path.</summary>
    public Vector3 Position { get; }

    /// <summary>Surface normal at <see cref="Position"/>, defaulting to world up when unprobed.</summary>
    public Vector3 GroundNormal { get; }

    /// <summary>Planned avatar facing yaw in radians (0 faces +Z) at this future time.</summary>
    public float FacingYawRadians { get; }

    /// <summary>Planned travel direction yaw in radians (0 moves along +Z) at this future time.</summary>
    public float TravelYawRadians { get; }

    /// <summary>Planned planar speed in meters per second at this future time.</summary>
    public float SpeedMetersPerSecond { get; }

    /// <summary>Lead time in seconds ahead of the current pose.</summary>
    public float TimeSeconds { get; }

    /// <summary>True when a wall or obstacle sweep deflected or clamped this trajectory step.</summary>
    public bool IsCollisionClamped { get; }

    /// <summary>True when a downward ground query found a surface below this trajectory step.</summary>
    public bool HasGroundHit { get; }
}
