using System.Collections;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class CpuRenderOcclusionCoordinatorTests
{
    private const int RenderPass = 0;
    private const ulong TestFrameId = 128UL;

    private IRuntimeRenderingHostServices _previousServices = null!;
    private TestHostServices _host = null!;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeRenderingHostServices.Current;
        RuntimeRenderingHostServices.Current = TestHostServices.Create(_previousServices, out _host);
        _host.CurrentRenderFrameId = TestFrameId;
    }

    [TearDown]
    public void TearDown()
        => RuntimeRenderingHostServices.Current = _previousServices;

    [Test]
    public void BeginPass_SmallMotionPreservesOccludedStateAndRequestsRecoveryProbe()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 43u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 8UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.SmallMotion);
        decision.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
        request.Requested.ShouldBeTrue();
        request.RecoveryProbe.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.CameraMotionRevalidation);
        coordinator.PeekShouldRender(RenderPass, camera, queryKey).ShouldBeFalse();
    }

    [Test]
    public void BeginPass_MediumMotionPreservesOccludedStateAndRequestsRecoveryProbe()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 44u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 3UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.5f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.MediumMotion);
        decision.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.CameraMotionRevalidation);
    }

    [Test]
    public void BeginPass_LargeMotionDoesNotResetOccludedState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 45u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 1UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.LargeMotion);
        decision.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.CameraMotionRevalidation);
    }

    [Test]
    public void BeginPass_CameraCutForcesConservativeVisibleFrame()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 46u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 1UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(20.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(RenderPass, camera, sceneCommandCount: 1u).ShouldBeTrue();

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.CameraCut);
        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();
    }

    [Test]
    public void BeginPass_VrHeadPoseMotionDoesNotActLikeCameraCut()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = CreateEyeCamera(leftEye: true);
        const uint queryKey = 47u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 3UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.05f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.VrHeadPoseMotion);
        decision.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.CameraMotionRevalidation);
    }

    [Test]
    public void CreatePassKey_UsesStableStereoScopesInsteadOfCameraIdentity()
    {
        XRCamera desktopA = new();
        XRCamera desktopB = new();
        XRCamera leftA = CreateEyeCamera(leftEye: true);
        XRCamera leftB = CreateEyeCamera(leftEye: true);
        XRCamera right = CreateEyeCamera(leftEye: false);

        OcclusionViewKey desktopKeyA = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, desktopA);
        OcclusionViewKey desktopKeyB = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, desktopB);
        OcclusionViewKey leftKeyA = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftA);
        OcclusionViewKey leftKeyB = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftB);
        OcclusionViewKey rightKey = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, right);

        desktopKeyA.ShouldBe(desktopKeyB);
        desktopKeyA.Scope.ShouldBe(EOcclusionViewScope.MonoDesktop);
        leftKeyA.ShouldBe(leftKeyB);
        leftKeyA.Scope.ShouldBe(EOcclusionViewScope.VrLeftEye);
        rightKey.Scope.ShouldBe(EOcclusionViewScope.VrRightEye);
        leftKeyA.ShouldNotBe(rightKey);

        _host.CpuQueryStereoMode = ECpuQueryStereoMode.StereoPairShared;
        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftA).Scope.ShouldBe(EOcclusionViewScope.VrStereoPair);

        _host.ActiveExecutionState = new TestExecutionState { StereoPass = true, RenderingCamera = leftA, StereoRightEyeCamera = right };
        _host.VrViewRenderMode = EVrViewRenderMode.SinglePassStereo;
        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftA).Scope.ShouldBe(EOcclusionViewScope.VrSinglePassStereo);

        _host.EnableVrFoveatedViewSet = true;
        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftA).Scope.ShouldBe(EOcclusionViewScope.VrFoveatedView);
    }

    [Test]
    public void BeginPass_RecreatedEyeCameraReusesExistingState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera firstLeft = CreateEyeCamera(leftEye: true);
        XRCamera recreatedLeft = CreateEyeCamera(leftEye: true);
        const uint queryKey = 48u;

        BeginPassForPolicyTest(coordinator, firstLeft);
        SeedOccludedQuery(coordinator, firstLeft, queryKey, TestFrameId - 1UL);

        BeginPassForPolicyTest(coordinator, recreatedLeft);
        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, recreatedLeft, queryKey, out CpuOcclusionProbeRequest request);

        decision.ShouldBe(ECpuOcclusionDecision.Skip);
        request.Requested.ShouldBeFalse();
    }

    [Test]
    public void BeginPass_SceneCountChangeDoesNotInvalidateExistingState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint existingKey = 49u;
        const uint newKey = 50u;

        BeginPassForPolicyTest(coordinator, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, camera, existingKey, TestFrameId - 1UL);

        BeginPassForPolicyTest(coordinator, camera, sceneCommandCount: 2u);
        ECpuOcclusionDecision existingDecision = coordinator.ShouldRender(RenderPass, camera, existingKey, out CpuOcclusionProbeRequest existingRequest);
        ECpuOcclusionDecision newDecision = coordinator.ShouldRender(RenderPass, camera, newKey, out CpuOcclusionProbeRequest newRequest);

        existingDecision.ShouldBe(ECpuOcclusionDecision.Skip);
        existingRequest.Requested.ShouldBeFalse();
        newDecision.ShouldBe(ECpuOcclusionDecision.Visible);
        newRequest.Requested.ShouldBeTrue();
        newRequest.Reason.ShouldBe(ECpuOcclusionQueryReason.InitialSeed);
    }

    [Test]
    public void SelectProbeCandidates_RespectsPerFrameBudget()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 4;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.25f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        BeginPassForPolicyTest(coordinator, camera);

        List<CpuOcclusionProbeCandidate> candidates = [];
        for (uint key = 64u; key < 76u; key++)
        {
            SeedOccludedQuery(coordinator, camera, key, TestFrameId - 8UL);
            candidates.Add(new CpuOcclusionProbeCandidate(
                key,
                UnitBounds(),
                new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.OccludedRecovery, recoveryProbe: true),
                screenPriority: key,
                distanceMeters: 1.0f));
        }

        List<CpuOcclusionScheduledProbe> scheduled = [];
        coordinator.SelectProbeCandidates(RenderPass, camera, candidates, scheduled);

        scheduled.Count.ShouldBeLessThanOrEqualTo(4);
    }

    [Test]
    public void SelectProbeCandidates_DoesNotSchedulePendingQuery()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 77u;

        BeginPassForPolicyTest(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 8UL);
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingSinceFrame", TestFrameId);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", false);

        List<CpuOcclusionProbeCandidate> candidates =
        [
            new(
                queryKey,
                UnitBounds(),
                new CpuOcclusionProbeRequest(true, ECpuOcclusionQueryReason.OccludedRecovery, recoveryProbe: true),
                screenPriority: 10.0f,
                distanceMeters: 1.0f),
        ];
        List<CpuOcclusionScheduledProbe> scheduled = [];

        coordinator.SelectProbeCandidates(RenderPass, camera, candidates, scheduled);

        scheduled.ShouldBeEmpty();
    }

    [Test]
    public void ShouldRender_OverduePendingQueryForcesVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 78u;

        BeginPassForPolicyTest(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 8UL);
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingSinceFrame", 1UL);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", false);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();
        GetNonPublicField(queryState, "QueryPending").ShouldBe(false);
        GetNonPublicField(queryState, "StateKind").ShouldBe(ECpuOcclusionQueryStateKind.ForcedVisible);
    }

    [Test]
    public void ForceVisible_NearPlaneUnsafeKeepsCommandVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 79u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 1UL);

        coordinator.ForceVisible(RenderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NearPlaneUnsafe);
        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();
    }

    [Test]
    public void BeginPass_UnsupportedBackendForcesVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 80u;

        coordinator.BeginPass(RenderPass, camera, sceneCommandCount: 1u);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 8UL);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();
    }

    private static void BeginPassForPolicyTest(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        uint sceneCommandCount = 1u)
    {
        coordinator.BeginPass(RenderPass, camera, sceneCommandCount);
        object passState = GetPassState(coordinator, camera);
        SetNonPublicField(passState, "ForceVisibleThisFrame", false);
        SetNonPublicField(passState, "ForceVisibleReason", ECpuOcclusionForceVisibleReason.None);
    }

    private static object SeedOccludedQuery(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        uint queryKey,
        ulong lastQueryFrame)
    {
        object passState = GetPassState(coordinator, camera);
        object queryState = InvokeNonPublic(coordinator, "GetOrCreateQueryState", passState, queryKey).ShouldNotBeNull();
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;

        SetNonPublicField(queryState, "StateKind", ECpuOcclusionQueryStateKind.PredictedOccluded);
        SetNonPublicField(queryState, "LastAnySamplesPassed", false);
        SetNonPublicField(queryState, "ConsecutiveVisibleFrames", 0);
        SetNonPublicField(queryState, "ConsecutiveOccludedFrames", 8);
        SetNonPublicField(queryState, "LastTouchedFrame", frameId);
        SetNonPublicField(queryState, "LastVisibleFrame", frameId > 16UL ? frameId - 16UL : 1UL);
        SetNonPublicField(queryState, "LastOccludedFrame", frameId);
        SetNonPublicField(queryState, "LastQueryFrame", lastQueryFrame);
        SetNonPublicField(queryState, "PendingSinceFrame", 0UL);
        SetNonPublicField(queryState, "QueryPending", false);
        SetNonPublicField(queryState, "DiscardPendingResult", false);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", false);
        SetNonPublicField(queryState, "PendingReason", ECpuOcclusionQueryReason.None);
        SetNonPublicField(queryState, "LastDecision", ECpuOcclusionDecision.Skip);
        SetNonPublicField(queryState, "LastProbeRequest", CpuOcclusionProbeRequest.None);
        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);
        SetNonPublicField(queryState, "QueryIssuedFrameId", ulong.MaxValue);

        IDictionary queries = (IDictionary)GetNonPublicField(passState, "Queries");
        queries[queryKey] = queryState;
        return queryState;
    }

    private static ECpuOcclusionMotionTier GetMotionTier(CpuRenderOcclusionCoordinator coordinator, XRCamera camera)
        => (ECpuOcclusionMotionTier)GetNonPublicField(GetPassState(coordinator, camera), "MotionTier");

    private static object GetPassState(CpuRenderOcclusionCoordinator coordinator, XRCamera camera)
        => InvokeNonPublic(coordinator, "GetPassState", RenderPass, camera).ShouldNotBeNull();

    private static object? InvokeNonPublic(object target, string methodName, params object?[] args)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        MethodInfo? method = target.GetType().GetMethod(methodName, flags);
        method.ShouldNotBeNull();
        return method.Invoke(target, args);
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

    private static XRCamera CreateEyeCamera(bool leftEye)
        => new() { Parameters = new XROVRCameraParameters(leftEye, 0.1f, 10000.0f) };

    private static AABB UnitBounds()
        => new(new Vector3(-0.5f), new Vector3(0.5f));

    private sealed class TestExecutionState : IRuntimeRenderCommandExecutionState
    {
        public IRuntimeViewportHost? WindowViewport => null;
        public IRuntimeRenderCommandSceneContext? RenderingScene => null;
        public IRuntimeRenderCamera? SceneCamera { get; init; }
        public IRuntimeRenderCamera? RenderingCamera { get; init; }
        public IRuntimeRenderCamera? StereoRightEyeCamera { get; init; }
        public bool StereoPass { get; init; }
    }

    private class TestHostServices : DispatchProxy
    {
        public IRuntimeRenderingHostServices Inner { get; set; } = null!;
        public ulong CurrentRenderFrameId { get; set; } = TestFrameId;
        public IRuntimeRenderCommandExecutionState? ActiveExecutionState { get; set; }
        public bool? IsStereoPass { get; set; }
        public bool? IsInVR { get; set; }
        public bool? RenderWindowsWhileInVR { get; set; }
        public bool? VrMirrorComposeFromEyeTextures { get; set; }
        public bool? EnableVrFoveatedViewSet { get; set; }
        public EVrViewRenderMode? VrViewRenderMode { get; set; }
        public ECpuQueryStereoMode? CpuQueryStereoMode { get; set; }
        public int? CpuQueryOcclusionMaxQueriesPerFrame { get; set; }
        public float? CpuQueryOcclusionVisibleDemotionBudgetFraction { get; set; }
        public int? CpuQueryOcclusionRecoveryMinCadenceFrames { get; set; }

        public static IRuntimeRenderingHostServices Create(
            IRuntimeRenderingHostServices inner,
            out TestHostServices state)
        {
            IRuntimeRenderingHostServices proxy = Create<IRuntimeRenderingHostServices, TestHostServices>();
            state = (TestHostServices)(object)proxy;
            state.Inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;

            string methodName = targetMethod.Name;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CurrentRenderFrameId)}")
                return CurrentRenderFrameId;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.ActiveRenderCommandExecutionState)}")
                return ActiveExecutionState;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.IsStereoPass)}" && IsStereoPass.HasValue)
                return IsStereoPass.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.IsInVR)}" && IsInVR.HasValue)
                return IsInVR.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.RenderWindowsWhileInVR)}" && RenderWindowsWhileInVR.HasValue)
                return RenderWindowsWhileInVR.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.VrMirrorComposeFromEyeTextures)}" && VrMirrorComposeFromEyeTextures.HasValue)
                return VrMirrorComposeFromEyeTextures.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.EnableVrFoveatedViewSet)}" && EnableVrFoveatedViewSet.HasValue)
                return EnableVrFoveatedViewSet.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.VrViewRenderMode)}" && VrViewRenderMode.HasValue)
                return VrViewRenderMode.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionStereoMode)}" && CpuQueryStereoMode.HasValue)
                return CpuQueryStereoMode.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionMaxQueriesPerFrame)}" && CpuQueryOcclusionMaxQueriesPerFrame.HasValue)
                return CpuQueryOcclusionMaxQueriesPerFrame.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionVisibleDemotionBudgetFraction)}" && CpuQueryOcclusionVisibleDemotionBudgetFraction.HasValue)
                return CpuQueryOcclusionVisibleDemotionBudgetFraction.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionRecoveryMinCadenceFrames)}" && CpuQueryOcclusionRecoveryMinCadenceFrames.HasValue)
                return CpuQueryOcclusionRecoveryMinCadenceFrames.Value;

            return targetMethod.Invoke(Inner, args);
        }
    }
}
