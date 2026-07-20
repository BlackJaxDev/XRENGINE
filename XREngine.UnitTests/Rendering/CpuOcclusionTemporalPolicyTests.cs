using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CpuOcclusionTemporalPolicyTests
{
    [Test]
    public void ClassifyMotion_UsesSmallThresholdAsStableAndNormalizesForRenderDelta()
    {
        CpuOcclusionCameraSnapshot origin = CreateSnapshot(Vector3.Zero);
        CpuOcclusionCameraSnapshot oneSixtieth = CreateSnapshot(new Vector3(0.015f, 0.0f, 0.0f));
        CpuOcclusionCameraSnapshot oneThirtieth = CreateSnapshot(new Vector3(0.030f, 0.0f, 0.0f));

        CpuOcclusionTemporalPolicy.ClassifyMotion(origin, oneSixtieth, vrScope: false, 1.0 / 60.0)
            .ShouldBe(ECpuOcclusionMotionTier.Stable);
        CpuOcclusionTemporalPolicy.ClassifyMotion(origin, oneThirtieth, vrScope: false, 1.0 / 30.0)
            .ShouldBe(ECpuOcclusionMotionTier.Stable);
        CpuOcclusionTemporalPolicy.ClassifyMotion(origin, oneThirtieth, vrScope: false, 1.0 / 60.0)
            .ShouldBe(ECpuOcclusionMotionTier.SmallMotion);
    }

    [Test]
    public void ComputeMaximumResultAge_IncludesMovingSceneSweepAndBackendLatency()
    {
        int maximumAge = CpuOcclusionTemporalPolicy.ComputeMaximumResultAge(
            ECpuOcclusionMotionTier.LargeMotion,
            retestPeriodFrames: 6,
            sceneCommandCount: 393u,
            maxQueriesPerFrame: 32,
            visibleBudgetFraction: 0.5f,
            recoveryMinCadenceFrames: 2,
            backendMinimumLatencyFrames: 2);

        // Large motion reserves 28 of 32 queries for recovery: ceil(393/28)
        // sweep frames + one-frame cadence + the three-frame latency floor.
        maximumAge.ShouldBe(19);
    }

    [Test]
    public void CanReuseNegativeResult_CentralFarProxySurvivesSmallAccumulatedParallax()
    {
        CpuOcclusionCameraSnapshot queryCamera = CreateSnapshot(Vector3.Zero);
        CpuOcclusionCameraSnapshot currentCamera = CreateSnapshot(new Vector3(0.25f, 0.0f, 0.0f));
        AABB bounds = AABB.FromCenterSize(new Vector3(0.0f, 0.0f, -10.0f), Vector3.One);

        CpuOcclusionTemporalPolicy.CanReuseNegativeResult(
            queryCamera,
            bounds,
            currentCamera,
            bounds,
            out float revealRisk).ShouldBeTrue();

        revealRisk.ShouldBeGreaterThan(0.0f);
    }

    [Test]
    public void CanReuseNegativeResult_RejectsLargeAccumulatedParallax()
    {
        CpuOcclusionCameraSnapshot queryCamera = CreateSnapshot(Vector3.Zero);
        CpuOcclusionCameraSnapshot currentCamera = CreateSnapshot(new Vector3(2.0f, 0.0f, 0.0f));
        AABB bounds = AABB.FromCenterSize(new Vector3(0.0f, 0.0f, -10.0f), Vector3.One);

        CpuOcclusionTemporalPolicy.CanReuseNegativeResult(
            queryCamera,
            bounds,
            currentCamera,
            bounds,
            out _).ShouldBeFalse();
    }

    [Test]
    public void CanReuseNegativeResult_RejectsViewportEdgeProxyEvenWithoutPoseChange()
    {
        CpuOcclusionCameraSnapshot camera = CreateSnapshot(Vector3.Zero);
        AABB bounds = AABB.FromCenterSize(new Vector3(9.0f, 0.0f, -10.0f), Vector3.One);

        CpuOcclusionTemporalPolicy.CanReuseNegativeResult(
            camera,
            bounds,
            camera,
            bounds,
            out _).ShouldBeFalse();
    }

    private static CpuOcclusionCameraSnapshot CreateSnapshot(Vector3 position)
    {
        Vector3 forward = -Vector3.UnitZ;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI * 0.5f,
            aspectRatio: 1.0f,
            nearPlaneDistance: 0.1f,
            farPlaneDistance: 100.0f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(position, position + forward, Vector3.UnitY);
        return new CpuOcclusionCameraSnapshot(
            position,
            forward,
            Vector3.UnitY,
            projection,
            view * projection,
            nearZ: 0.1f);
    }
}
