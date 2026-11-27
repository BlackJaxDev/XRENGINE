using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data;
using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine.Components.Animation;

/// <summary>
/// Probes static physics geometry (Jolt or PhysX) in front of a character to measure
/// ledge height, ledge depth, and landing clearance for vault, mantle, and climb
/// <see cref="XREngine.Animation.MotionWarpingMotion"/> targets.
/// </summary>
public sealed class PhysicsTraversalProber
{
    private static readonly PhysicsQueryFilter StaticFilter = new(
        PhysicsQueryActorTypes.Static,
        PhysicsQueryHitDetail.Position | PhysicsQueryHitDetail.Normal);

    private readonly SortedDictionary<float, List<(XRComponent? item, object? data)>> _sweepScratch = [];

    /// <summary>
    /// Detects a climbable or vaultable obstacle ahead of <paramref name="characterFootPosition"/>
    /// along <paramref name="forwardDirectionXZ"/>.
    /// </summary>
    public TraversalLedgeProbeResult ProbeLedge(
        AbstractPhysicsScene physicsScene,
        Vector3 characterFootPosition,
        Vector3 forwardDirectionXZ,
        float maxReachDistance = 1.25f,
        float minLedgeHeight = 0.40f,
        float maxLedgeHeight = 2.20f,
        float probeRadius = 0.16f,
        float maxVaultDepth = 1.20f)
    {
        ArgumentNullException.ThrowIfNull(physicsScene);

        var forward = new Vector3(forwardDirectionXZ.X, 0.0f, forwardDirectionXZ.Z);
        float fwdLenSq = forward.LengthSquared();
        if (fwdLenSq <= 1.0e-6f)
            return default;

        forward /= MathF.Sqrt(fwdLenSq);
        float radius = Math.Clamp(probeRadius, 0.08f, 0.35f);

        // 1. Forward sweep at waist height to locate the obstacle front face.
        Vector3 waistStart = characterFootPosition + new Vector3(0.0f, MathF.Max(minLedgeHeight, 0.55f), 0.0f);
        if (!TrySweepSingle(
            physicsScene,
            new IPhysicsGeometry.Sphere(radius),
            waistStart,
            forward,
            maxReachDistance,
            out float faceDist,
            out Vector3 faceNormal))
        {
            return default;
        }

        if (MathF.Abs(faceNormal.Y) > 0.45f)
            return default;

        Vector3 frontFacePoint = waistStart + forward * (faceDist + radius);
        Vector3 insetForward = new(-faceNormal.X, 0.0f, -faceNormal.Z);
        insetForward = insetForward.LengthSquared() > 1.0e-6f ? Vector3.Normalize(insetForward) : forward;

        // 2. Downward sweep from above maxLedgeHeight slightly inside the obstacle face to find top surface.
        float topStartY = characterFootPosition.Y + maxLedgeHeight + radius + 0.25f;
        Vector3 topProbeXZ = new Vector3(frontFacePoint.X, 0.0f, frontFacePoint.Z) + insetForward * MathF.Max(0.12f, radius);
        Vector3 topSweepStart = new(topProbeXZ.X, topStartY, topProbeXZ.Z);
        float maxDown = (maxLedgeHeight - minLedgeHeight) + 0.50f;

        if (!TrySweepSingle(
            physicsScene,
            new IPhysicsGeometry.Sphere(radius * 0.75f),
            topSweepStart,
            -Vector3.UnitY,
            maxDown,
            out float topDist,
            out Vector3 topNormal))
        {
            return default;
        }

        if (topNormal.Y < 0.65f)
            return default;

        float topY = topStartY - topDist - radius * 0.75f;
        float ledgeHeight = topY - characterFootPosition.Y;
        if (ledgeHeight < minLedgeHeight || ledgeHeight > maxLedgeHeight)
            return default;

        Vector3 ledgeTopPos = new(topProbeXZ.X, topY, topProbeXZ.Z);

        // 3. Measure obstacle depth and check for landing floor past the far edge.
        float measuredDepth = 0.0f;
        bool hasLanding = false;
        Vector3 landingPos = ledgeTopPos;

        const int depthSamples = 6;
        float stepDepth = MathF.Max(0.15f, maxVaultDepth / depthSamples);
        for (int s = 1; s <= depthSamples; s++)
        {
            float d = s * stepDepth;
            Vector3 probePos = new Vector3(topProbeXZ.X, topY + 0.35f, topProbeXZ.Z) + insetForward * d;
            if (TrySweepSingle(
                physicsScene,
                new IPhysicsGeometry.Sphere(radius * 0.65f),
                probePos,
                -Vector3.UnitY,
                ledgeHeight + 1.25f,
                out float downDist,
                out Vector3 downNormal))
            {
                float surfaceY = probePos.Y - downDist - radius * 0.65f;
                if (surfaceY >= topY - 0.15f)
                {
                    measuredDepth = d;
                    continue;
                }

                if (downNormal.Y >= 0.65f)
                {
                    hasLanding = true;
                    landingPos = new Vector3(probePos.X, surfaceY, probePos.Z);
                    break;
                }
            }
        }

        return new TraversalLedgeProbeResult(
            Found: true,
            FrontFacePoint: new Vector3(frontFacePoint.X, characterFootPosition.Y, frontFacePoint.Z),
            FrontFaceNormal: faceNormal,
            LedgeTopPosition: ledgeTopPos,
            LedgeHeightMeters: ledgeHeight,
            LedgeDepthMeters: measuredDepth,
            HasLandingClearance: hasLanding,
            LandingPosition: landingPos);
    }

    private bool TrySweepSingle(
        AbstractPhysicsScene physicsScene,
        IPhysicsGeometry geometry,
        Vector3 startPosition,
        Vector3 unitDirection,
        float distance,
        out float hitDistance,
        out Vector3 hitNormal)
    {
        hitDistance = distance;
        hitNormal = Vector3.UnitY;
        _sweepScratch.Clear();

        bool hit = physicsScene.SweepSingle(
            geometry,
            (startPosition, Quaternion.Identity),
            unitDirection,
            distance,
            LayerMask.Everything,
            StaticFilter,
            _sweepScratch);
        if (!hit || _sweepScratch.Count == 0)
            return false;

        foreach ((float dist, List<(XRComponent? item, object? data)> hits) in _sweepScratch)
        {
            if (!float.IsFinite(dist) || hits.Count == 0)
                continue;

            hitDistance = dist;
            if (hits[0].data is SweepHit sweepHit && sweepHit.Normal.LengthSquared() > 1.0e-6f)
                hitNormal = Vector3.Normalize(sweepHit.Normal);

            return true;
        }

        return false;
    }
}
