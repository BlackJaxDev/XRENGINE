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
    public void BeginPass_SmallCameraTranslationPreservesOccludedState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

        decision.ShouldNotBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBe(decision == ECpuOcclusionDecision.ProbeOnly);
    }

    [Test]
    public void BeginPass_SmallCameraRotationPreservesOccludedState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateRotationY(0.01f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

        decision.ShouldNotBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBe(decision == ECpuOcclusionDecision.ProbeOnly);
    }

    [Test]
    public void BeginPass_LargeCameraTranslationInvalidatesOccludedStateAsVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 43u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u).ShouldBeTrue();

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

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
        object queryState = SeedOccludedQuery(coordinator, renderPass, queryKey);
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", true);
        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

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
        object queryState = SeedOccludedQuery(coordinator, renderPass, queryKey);
        SetNonPublicField(queryState, "QueryPending", true);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeFalse();
    }

    [Test]
    public void ShouldRender_SmallCameraMotionRetestsOccludedItemsMoreOften()
    {
        int stablePeriod = Math.Clamp(RuntimeEngine.EffectiveSettings.CpuQueryOcclusionRetestPeriodFrames, 1, 64);
        int movingPeriod = Math.Max(1, (stablePeriod + 1) / 2);
        if (movingPeriod == stablePeriod)
            return;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        uint queryKey = FindRetestKey(frameId, stablePeriod, movingPeriod);

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        object queryState = SeedOccludedQuery(coordinator, renderPass, queryKey);

        ECpuOcclusionDecision stableDecision = coordinator.ShouldRender(renderPass, queryKey, out bool stableNeedsQuery);
        stableDecision.ShouldBe(ECpuOcclusionDecision.Skip);
        stableNeedsQuery.ShouldBeFalse();

        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);
        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision movingDecision = coordinator.ShouldRender(renderPass, queryKey, out bool movingNeedsQuery);

        movingDecision.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
        movingNeedsQuery.ShouldBeTrue();
    }

    private static object SeedOccludedQuery(CpuRenderOcclusionCoordinator coordinator, int renderPass, uint queryKey)
    {
        object passState = GetPassState(coordinator, renderPass);
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

    private static object GetPassState(CpuRenderOcclusionCoordinator coordinator, int renderPass)
    {
        IDictionary passStates = (IDictionary)GetNonPublicField(coordinator, "_passStates");
        passStates.Contains(renderPass).ShouldBeTrue();
        return passStates[renderPass]!;
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

    private static uint FindRetestKey(ulong frameId, int stablePeriod, int movingPeriod)
    {
        for (uint key = 1u; key < 1024u; key++)
        {
            ulong frameAndKey = frameId + key;
            if (frameAndKey % (ulong)movingPeriod == 0UL &&
                frameAndKey % (ulong)stablePeriod != 0UL)
            {
                return key;
            }
        }

        Assert.Fail($"Could not find a query key for stablePeriod={stablePeriod}, movingPeriod={movingPeriod}, frameId={frameId}.");
        return 0u;
    }
}
