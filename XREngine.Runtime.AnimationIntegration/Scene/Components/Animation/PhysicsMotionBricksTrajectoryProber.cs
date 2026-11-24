using System.Numerics;
using XREngine.Animation;
using XREngine.Data;
using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine.Components;

/// <summary>
/// Queries the active physics scene (Jolt or PhysX static geometry) to project future
/// MotionBricks ground trajectory points onto terrain surfaces and deflect them along
/// upcoming walls and pillars.
/// </summary>
internal sealed class PhysicsMotionBricksTrajectoryProber : IMotionBricksTrajectoryProber
{
    private const float ObstacleClearanceMeters = 0.03f;
    private const float GroundProbeRadiusMeters = 0.14f;
    private const float GroundProbeStartHeightMeters = 0.65f;
    private const float GroundProbeMaxDepthMeters = 1.85f;
    private const float MaxWallNormalY = 0.55f;

    private static readonly PhysicsQueryFilter StaticQueryFilter = new(
        PhysicsQueryActorTypes.Static,
        PhysicsQueryHitDetail.Position | PhysicsQueryHitDetail.Normal);

    private readonly Func<IRuntimePhysicsWorldContext?> _worldProvider;
    private readonly object _querySync = new();
    private readonly SortedDictionary<float, List<(XRComponent? item, object? data)>> _sweepScratch = [];

    public PhysicsMotionBricksTrajectoryProber(Func<IRuntimePhysicsWorldContext?> worldProvider)
        => _worldProvider = worldProvider;

    public bool ProbeTrajectoryStep(
        Vector3 fromGround,
        Vector3 desiredStepXZ,
        float capsuleRadius,
        float capsuleHalfHeight,
        out Vector3 resolvedGround,
        out Vector3 groundNormal,
        out bool hitObstacle,
        out Vector3 obstacleNormal)
    {
        resolvedGround = fromGround + new Vector3(desiredStepXZ.X, 0.0f, desiredStepXZ.Z);
        groundNormal = Vector3.UnitY;
        hitObstacle = false;
        obstacleNormal = Vector3.Zero;

        IRuntimePhysicsWorldContext? physicsWorld = _worldProvider();
        if (physicsWorld is null)
            return false;

        float stepLength = MathF.Sqrt(desiredStepXZ.X * desiredStepXZ.X + desiredStepXZ.Z * desiredStepXZ.Z);
        float radius = Math.Clamp(capsuleRadius, 0.15f, 0.40f);
        float chestHeight = Math.Clamp(capsuleHalfHeight * 0.78f, 0.45f, 0.95f);

        if (stepLength > 1.0e-4f)
        {
            Vector3 unitDir = new(desiredStepXZ.X / stepLength, 0.0f, desiredStepXZ.Z / stepLength);
            Vector3 sweepStart = new(fromGround.X, fromGround.Y + chestHeight, fromGround.Z);

            if (TrySweepSingle(
                physicsWorld.PhysicsScene,
                new IPhysicsGeometry.Sphere(radius),
                sweepStart,
                unitDir,
                stepLength + ObstacleClearanceMeters,
                out float hitDistance,
                out Vector3 hitNormal)
                && hitDistance <= stepLength
                && MathF.Abs(hitNormal.Y) < MaxWallNormalY)
            {
                hitObstacle = true;
                obstacleNormal = hitNormal;

                float advance = MathF.Max(0.0f, hitDistance - ObstacleClearanceMeters);
                Vector3 contactXZ = new(
                    fromGround.X + unitDir.X * advance,
                    fromGround.Y,
                    fromGround.Z + unitDir.Z * advance);

                // Slide any remaining horizontal travel along the obstacle wall tangent.
                float remaining = MathF.Max(0.0f, stepLength - advance);
                var normalXZ = new Vector2(hitNormal.X, hitNormal.Z);
                float normalLenSq = normalXZ.LengthSquared();
                if (remaining > 1.0e-3f && normalLenSq > 1.0e-6f)
                {
                    normalXZ /= MathF.Sqrt(normalLenSq);
                    var dirXZ = new Vector2(unitDir.X, unitDir.Z);
                    float intoWall = Vector2.Dot(dirXZ, normalXZ);
                    Vector2 slideXZ = dirXZ - normalXZ * intoWall;
                    float slideLen = slideXZ.Length();
                    if (slideLen > 0.15f)
                    {
                        Vector3 slideDir = new(slideXZ.X / slideLen, 0.0f, slideXZ.Y / slideLen);
                        float slideDist = remaining * slideLen;
                        Vector3 slideStart = new(contactXZ.X, fromGround.Y + chestHeight, contactXZ.Z);
                        if (TrySweepSingle(
                            physicsWorld.PhysicsScene,
                            new IPhysicsGeometry.Sphere(radius),
                            slideStart,
                            slideDir,
                            slideDist + ObstacleClearanceMeters,
                            out float slideHitDist,
                            out _))
                        {
                            slideDist = MathF.Max(0.0f, MathF.Min(slideDist, slideHitDist - ObstacleClearanceMeters));
                        }

                        contactXZ += slideDir * slideDist;
                    }
                }

                resolvedGround = contactXZ;
            }
        }

        if (ProbeGroundHeight(resolvedGround, GroundProbeStartHeightMeters, GroundProbeMaxDepthMeters, out float groundY, out Vector3 probedNormal))
        {
            resolvedGround.Y = groundY;
            groundNormal = probedNormal;
            return true;
        }

        return false;
    }

    public bool ProbeGroundHeight(
        Vector3 groundPosition,
        float probeUpOffset,
        float probeDownDistance,
        out float groundY,
        out Vector3 groundNormal)
    {
        groundY = groundPosition.Y;
        groundNormal = Vector3.UnitY;

        IRuntimePhysicsWorldContext? physicsWorld = _worldProvider();
        if (physicsWorld is null)
            return false;

        float startY = groundPosition.Y + MathF.Max(0.15f, probeUpOffset);
        float maxDist = MathF.Max(0.25f, probeDownDistance);
        Vector3 start = new(groundPosition.X, startY, groundPosition.Z);

        if (!TrySweepSingle(
            physicsWorld.PhysicsScene,
            new IPhysicsGeometry.Sphere(GroundProbeRadiusMeters),
            start,
            -Vector3.UnitY,
            maxDist,
            out float hitDistance,
            out Vector3 hitNormal))
            return false;

        groundY = startY - hitDistance - GroundProbeRadiusMeters;
        groundNormal = hitNormal.LengthSquared() > 1.0e-6f ? Vector3.Normalize(hitNormal) : Vector3.UnitY;
        return true;
    }

    private bool TrySweepSingle(
        AbstractPhysicsScene physicsScene,
        IPhysicsGeometry geometry,
        Vector3 startPosition,
        Vector3 unitDirection,
        float distance,
        out float nearestDistance,
        out Vector3 hitNormal)
    {
        nearestDistance = distance;
        hitNormal = Vector3.UnitY;

        lock (_querySync)
        {
            _sweepScratch.Clear();
            bool any = physicsScene.SweepSingle(
                geometry,
                (startPosition, Quaternion.Identity),
                unitDirection,
                distance,
                LayerMask.Everything,
                StaticQueryFilter,
                _sweepScratch);

            if (!any || _sweepScratch.Count == 0)
                return false;

            foreach ((float hitDist, List<(XRComponent? item, object? data)> hits) in _sweepScratch)
            {
                if (!float.IsFinite(hitDist) || hits.Count == 0)
                    continue;

                nearestDistance = hitDist;
                if (hits[0].data is SweepHit sweepHit && sweepHit.Normal.LengthSquared() > 1.0e-6f)
                    hitNormal = Vector3.Normalize(sweepHit.Normal);

                return true;
            }
        }

        return false;
    }
}
