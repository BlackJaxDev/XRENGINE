using System.Numerics;
using XREngine.Animation;

namespace XREngine.Components.Animation;

/// <summary>
/// Result produced by <see cref="PhysicsTraversalProber"/> describing a vaultable or climbable
/// ledge surface and optional landing ground point behind the obstacle.
/// </summary>
public readonly record struct TraversalLedgeProbeResult(
    bool Found,
    Vector3 FrontFacePoint,
    Vector3 FrontFaceNormal,
    Vector3 LedgeTopPosition,
    float LedgeHeightMeters,
    float LedgeDepthMeters,
    bool HasLandingClearance,
    Vector3 LandingPosition)
{
    public MotionWarpTarget ToLedgeWarpTarget(string targetName = "LedgeTop")
    {
        Vector3 facing = new(-FrontFaceNormal.X, 0.0f, -FrontFaceNormal.Z);
        float yaw = facing.LengthSquared() > 1.0e-6f ? MathF.Atan2(facing.X, facing.Z) : 0.0f;
        return new MotionWarpTarget(
            targetName,
            LedgeTopPosition,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw),
            HasTarget: Found);
    }
}
