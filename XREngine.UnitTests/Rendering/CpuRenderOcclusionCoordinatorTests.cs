using System.Collections;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CpuRenderOcclusionCoordinatorTests
{
    [Test]
    public void BeginPass_SmallCameraTranslationKeepsOccludedStateVisibleWhileMoving()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, camera, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeTrue();
        coordinator.PeekShouldRender(renderPass, camera, queryKey).ShouldBeTrue();
    }

    [Test]
    public void BeginPass_SmallCameraRotationKeepsOccludedStateVisibleWhileMoving()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, camera, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateRotationY(0.01f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeTrue();
    }

    [Test]
    public void BeginPass_LargeCameraTranslationInvalidatesOccludedStateAsVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, camera, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u).ShouldBeTrue();

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeTrue();
    }

    [Test]
    public void ShouldRender_PendingVisibleQueryKeepsMeshVisibleWithoutReissuing()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 42u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        object queryState = SeedOccludedQuery(coordinator, renderPass, camera, queryKey);
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", true);
        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeFalse();
    }

    [Test]
    public void BeginPass_LargeCameraTranslationKeepsPendingInvalidationVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 42u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        object queryState = SeedOccludedQuery(coordinator, renderPass, camera, queryKey);
        SetNonPublicField(queryState, "QueryPending", true);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeFalse();
    }

    [Test]
    public void ShouldRender_SmallCameraMotionKeepsOccludedItemsVisibleAndRequeries()
    {
        int stablePeriod = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames, 1, 64);
        if (stablePeriod <= 1)
            Assert.Inconclusive("A retest period of 1 has no stable skip frame to compare against.");

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        uint queryKey = FindNonRetestKey(frameId, stablePeriod);

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        object queryState = SeedOccludedQuery(coordinator, renderPass, camera, queryKey);

        ECpuOcclusionDecision stableDecision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool stableNeedsQuery);
        stableDecision.ShouldBe(ECpuOcclusionDecision.Skip);
        stableNeedsQuery.ShouldBeFalse();

        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);
        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision movingDecision = coordinator.ShouldRender(renderPass, camera, queryKey, out bool movingNeedsQuery);

        movingDecision.ShouldBe(ECpuOcclusionDecision.Visible);
        movingNeedsQuery.ShouldBeTrue();
    }

    [Test]
    public void ShouldRender_DifferentCameraDoesNotReuseOccludedStateForSameRenderPass()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera desktopCamera = new();
        XRCamera eyeCamera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, desktopCamera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, desktopCamera, queryKey);

        coordinator.BeginPass(renderPass, eyeCamera, sceneCommandCount: 1u);
        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, eyeCamera, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeTrue();
    }

    private static object SeedOccludedQuery(CpuRenderOcclusionCoordinator coordinator, int renderPass, XRCamera camera, uint queryKey)
    {
        object passState = GetPassState(coordinator, renderPass, camera);
        object queryState = CreateQueryState();

        SetNonPublicField(queryState, "LastAnySamplesPassed", false);
        SetNonPublicField(queryState, "ConsecutiveOccludedFrames", 8);
        SetNonPublicField(queryState, "LastDecision", ECpuOcclusionDecision.Skip);
        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);
        SetNonPublicField(queryState, "QueryIssuedFrameId", 0UL);

        IDictionary queries = (IDictionary)GetNonPublicField(passState, "Queries");
        queries[queryKey] = queryState;
        return queryState;
    }

    private static object GetPassState(CpuRenderOcclusionCoordinator coordinator, int renderPass, XRCamera camera)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo? method = typeof(CpuRenderOcclusionCoordinator).GetMethod("GetPassState", flags);
        method.ShouldNotBeNull();
        return method.Invoke(coordinator, [renderPass, camera]).ShouldNotBeNull();
    }

    private static object CreateQueryState()
    {
        Type queryStateType = typeof(CpuRenderOcclusionCoordinator)
            .GetNestedType("QueryState", BindingFlags.NonPublic)
            .ShouldNotBeNull();

        return Activator.CreateInstance(queryStateType, nonPublic: true).ShouldNotBeNull();
    }

    private static object GetNonPublicField(object target, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo? field = target.GetType().GetField(fieldName, flags);
        field.ShouldNotBeNull();
        return field.GetValue(target).ShouldNotBeNull();
    }

    private static void SetNonPublicField(object target, string fieldName, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo? field = target.GetType().GetField(fieldName, flags);
        field.ShouldNotBeNull();
        field.SetValue(target, value);
    }

    private static uint FindNonRetestKey(ulong frameId, int stablePeriod)
    {
        for (uint key = 1u; key < 1024u; key++)
        {
            if ((frameId + key) % (ulong)stablePeriod != 0UL)
            {
                return key;
            }
        }

        Assert.Fail($"Could not find a query key outside stablePeriod={stablePeriod}, frameId={frameId}.");
        return 0u;
    }
}
