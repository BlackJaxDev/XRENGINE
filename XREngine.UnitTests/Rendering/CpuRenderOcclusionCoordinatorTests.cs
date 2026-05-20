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
    public void BeginPass_CameraTranslationInvalidatesOccludedStateAsVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 42u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeTrue();
    }

    [Test]
    public void BeginPass_CameraRotationInvalidatesOccludedStateAsVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 42u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, renderPass, queryKey);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateRotationY(0.01f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

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
    public void BeginPass_CameraTranslationKeepsPendingInvalidationVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const int renderPass = 0;
        const uint queryKey = 42u;

        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);
        object queryState = SeedOccludedQuery(coordinator, renderPass, queryKey);
        SetNonPublicField(queryState, "QueryPending", true);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(renderPass, camera, sceneCommandCount: 1u);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        needsHardwareQuery.ShouldBeFalse();
    }

    private static object SeedOccludedQuery(CpuRenderOcclusionCoordinator coordinator, int renderPass, uint queryKey)
    {
        object passState = GetPassState(coordinator, renderPass);
        object queryState = CreateQueryState();

        SetNonPublicField(queryState, "LastAnySamplesPassed", false);
        SetNonPublicField(queryState, "ConsecutiveOccludedFrames", 8);
        SetNonPublicField(queryState, "LastDecision", ECpuOcclusionDecision.Skip);
        SetNonPublicField(queryState, "LastDecidedFrameId", 0UL);
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
}
