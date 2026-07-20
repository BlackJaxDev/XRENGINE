using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Shared motion, result-age, and query-pose validation policy for asynchronous
/// CPU hardware-query occlusion paths.
/// </summary>
internal static class CpuOcclusionTemporalPolicy
{
    internal const float StableProjectionDelta = 0.001f;
    internal const float ProjectionCutDelta = 0.125f;
    private const double NominalRenderDeltaSeconds = 1.0 / 60.0;
    private const float ViewportEdgeGuardNdc = 0.075f;
    private const float MaximumCenterShiftNdc = 0.12f;
    private const float MaximumExtentGrowthNdc = 0.10f;
    private const float MaximumMeaningfulExtentScale = 1.30f;

    internal static ECpuOcclusionMotionTier ClassifyMotion(
        in CpuOcclusionCameraSnapshot previous,
        in CpuOcclusionCameraSnapshot current,
        bool vrScope,
        double renderDeltaSeconds)
    {
        if (!previous.IsValid || !current.IsValid)
            return ECpuOcclusionMotionTier.CameraCut;

        float distance = Vector3.Distance(previous.Position, current.Position);
        float angle = MathF.Max(
            DotToDegrees(Vector3.Dot(previous.Forward, current.Forward)),
            DotToDegrees(Vector3.Dot(previous.Up, current.Up)));
        float projectionDelta = GetProjectionDelta(previous.Projection, current.Projection);

        if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutMeters ||
            angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutRotationDegrees ||
            projectionDelta > ProjectionCutDelta)
        {
            return ECpuOcclusionMotionTier.CameraCut;
        }

        float thresholdScale = GetMotionThresholdScale(renderDeltaSeconds);
        if (distance <= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionSmallMotionMeters * thresholdScale &&
            angle <= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionSmallRotationDegrees * thresholdScale &&
            projectionDelta <= StableProjectionDelta)
        {
            return ECpuOcclusionMotionTier.Stable;
        }

        if (vrScope &&
            distance <= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVrHeadMotionMeters * thresholdScale &&
            angle <= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionVrHeadRotationDegrees * thresholdScale)
        {
            return ECpuOcclusionMotionTier.VrHeadPoseMotion;
        }

        if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionLargeMotionMeters * thresholdScale ||
            angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionLargeRotationDegrees * thresholdScale)
        {
            return ECpuOcclusionMotionTier.LargeMotion;
        }

        if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMediumMotionMeters * thresholdScale ||
            angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionMediumRotationDegrees * thresholdScale)
        {
            return ECpuOcclusionMotionTier.MediumMotion;
        }

        return ECpuOcclusionMotionTier.SmallMotion;
    }

    /// <summary>
    /// Rejects an old zero-sample result when the queried proxy no longer has a
    /// sufficiently similar screen footprint. This is intentionally a rejection
    /// test: a Boolean query has no occluder depth to reproject and cannot prove
    /// future occlusion on its own.
    /// </summary>
    internal static bool CanReuseNegativeResult(
        in CpuOcclusionCameraSnapshot queryCamera,
        in AABB queriedBounds,
        in CpuOcclusionCameraSnapshot currentCamera,
        in AABB currentBounds,
        out float revealRisk)
    {
        revealRisk = float.PositiveInfinity;
        if (!queryCamera.IsValid || !currentCamera.IsValid || !queriedBounds.IsValid || !currentBounds.IsValid)
            return false;

        float distance = Vector3.Distance(queryCamera.Position, currentCamera.Position);
        float angle = MathF.Max(
            DotToDegrees(Vector3.Dot(queryCamera.Forward, currentCamera.Forward)),
            DotToDegrees(Vector3.Dot(queryCamera.Up, currentCamera.Up)));
        float projectionDelta = GetProjectionDelta(queryCamera.Projection, currentCamera.Projection);
        if (distance >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutMeters ||
            angle >= RuntimeEngine.EffectiveSettings.CpuQueryOcclusionCameraCutRotationDegrees ||
            projectionDelta > ProjectionCutDelta)
        {
            return false;
        }

        if (!CpuOcclusionProjectionFootprint.TryProject(queriedBounds, queryCamera.ViewProjection, out CpuOcclusionProjectionFootprint queried) ||
            !CpuOcclusionProjectionFootprint.TryProject(currentBounds, currentCamera.ViewProjection, out CpuOcclusionProjectionFootprint current) ||
            !queried.IntersectsViewport ||
            !current.IntersectsViewport)
        {
            return false;
        }

        float centerShift = Vector2.Distance(queried.Center, current.Center);
        float widthGrowth = current.Width - queried.Width;
        float heightGrowth = current.Height - queried.Height;
        float extentGrowth = MathF.Max(widthGrowth, heightGrowth);
        float queriedExtent = MathF.Max(queried.Width, queried.Height);
        float currentExtent = MathF.Max(current.Width, current.Height);
        float extentScale = currentExtent / MathF.Max(0.001f, queriedExtent);
        float edgeMargin = MathF.Min(queried.ViewportEdgeMargin, current.ViewportEdgeMargin);
        float edgePressure = Math.Clamp(
            (ViewportEdgeGuardNdc - edgeMargin) / ViewportEdgeGuardNdc,
            0.0f,
            1.0f);

        revealRisk =
            centerShift * 8.0f +
            MathF.Max(0.0f, extentGrowth) * 6.0f +
            edgePressure * 3.0f +
            Math.Clamp(projectionDelta / ProjectionCutDelta, 0.0f, 1.0f);

        if (edgeMargin < ViewportEdgeGuardNdc ||
            centerShift > MaximumCenterShiftNdc ||
            extentGrowth > MaximumExtentGrowthNdc)
        {
            return false;
        }

        return queriedExtent < 0.03f || extentScale <= MaximumMeaningfulExtentScale;
    }

    internal static int ComputeMaximumResultAge(
        ECpuOcclusionMotionTier tier,
        int retestPeriodFrames,
        uint sceneCommandCount,
        int maxQueriesPerFrame,
        float visibleBudgetFraction,
        int recoveryMinCadenceFrames,
        int backendMinimumLatencyFrames)
    {
        int baseAge = GetBaseMaximumResultAge(tier, retestPeriodFrames, recoveryMinCadenceFrames);
        int latencyFloor = Math.Max(1, backendMinimumLatencyFrames) + 1;
        int maximumAge = Math.Max(baseAge, latencyFloor);
        if (sceneCommandCount == 0u || maxQueriesPerFrame <= 0)
            return maximumAge;

        ComputeBudgets(tier, maxQueriesPerFrame, visibleBudgetFraction, out _, out int recoveryBudget);
        if (recoveryBudget <= 0)
            return maximumAge;

        long commandCount = sceneCommandCount;
        long sweepFrames = Math.Max(1L, (commandCount + recoveryBudget - 1L) / recoveryBudget);
        int cadence = GetRecoveryCadence(tier, retestPeriodFrames, recoveryMinCadenceFrames);
        long capacityAge = sweepFrames + cadence + latencyFloor;
        if (capacityAge >= int.MaxValue)
            return int.MaxValue;

        return Math.Max(maximumAge, (int)capacityAge);
    }

    internal static void ComputeBudgets(
        ECpuOcclusionMotionTier tier,
        int maxQueries,
        float visibleFraction,
        out int visibleBudget,
        out int recoveryBudget)
    {
        maxQueries = Math.Max(0, maxQueries);
        visibleBudget = Math.Clamp((int)MathF.Round(maxQueries * visibleFraction), 0, maxQueries);
        if (tier is ECpuOcclusionMotionTier.MediumMotion or ECpuOcclusionMotionTier.LargeMotion or ECpuOcclusionMotionTier.VrHeadPoseMotion)
            visibleBudget = Math.Min(visibleBudget, Math.Max(1, maxQueries / 8));
        recoveryBudget = Math.Max(0, maxQueries - visibleBudget);
    }

    internal static int GetRecoveryCadence(
        ECpuOcclusionMotionTier tier,
        int retestPeriodFrames,
        int recoveryMinCadenceFrames)
    {
        int period = Math.Max(1, retestPeriodFrames);
        int minCadence = Math.Max(1, recoveryMinCadenceFrames);
        return tier switch
        {
            ECpuOcclusionMotionTier.Stable => Math.Max(minCadence, period),
            ECpuOcclusionMotionTier.SmallMotion => Math.Max(minCadence, (period + 1) / 2),
            ECpuOcclusionMotionTier.MediumMotion => minCadence,
            ECpuOcclusionMotionTier.LargeMotion => 1,
            ECpuOcclusionMotionTier.VrHeadPoseMotion => minCadence,
            _ => period,
        };
    }

    internal static float GetProjectionDelta(in Matrix4x4 previous, in Matrix4x4 current)
        => MathF.Abs(previous.M11 - current.M11) +
           MathF.Abs(previous.M22 - current.M22) +
           MathF.Abs(previous.M31 - current.M31) +
           MathF.Abs(previous.M32 - current.M32) +
           MathF.Abs(previous.M33 - current.M33) +
           MathF.Abs(previous.M43 - current.M43);

    private static int GetBaseMaximumResultAge(
        ECpuOcclusionMotionTier tier,
        int retestPeriodFrames,
        int recoveryMinCadenceFrames)
    {
        int period = Math.Max(1, retestPeriodFrames);
        return tier switch
        {
            ECpuOcclusionMotionTier.Stable => Math.Max(2, period * 2),
            ECpuOcclusionMotionTier.SmallMotion => Math.Max(2, period),
            ECpuOcclusionMotionTier.MediumMotion => Math.Max(1, period / 2),
            ECpuOcclusionMotionTier.LargeMotion => 1,
            ECpuOcclusionMotionTier.VrHeadPoseMotion => Math.Max(1, recoveryMinCadenceFrames),
            _ => 1,
        };
    }

    private static float GetMotionThresholdScale(double renderDeltaSeconds)
    {
        if (!double.IsFinite(renderDeltaSeconds) || renderDeltaSeconds <= 0.0)
            return 1.0f;

        return (float)Math.Clamp(renderDeltaSeconds / NominalRenderDeltaSeconds, 0.25, 4.0);
    }

    private static float DotToDegrees(float dot)
        => MathF.Acos(Math.Clamp(dot, -1.0f, 1.0f)) * (180.0f / MathF.PI);
}
