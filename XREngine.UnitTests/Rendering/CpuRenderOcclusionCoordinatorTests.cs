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
    public void BeginPass_SubSmallThresholdMotionIsStableAndRetainsOcclusion()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 43u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 8UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.01f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.Stable);
        decision.ShouldBe(ECpuOcclusionDecision.Skip);
        request.Requested.ShouldBeFalse();
        coordinator.PeekShouldRender(RenderPass, camera, queryKey).ShouldBeFalse();
    }

    [Test]
    public void ContinuousSmallMotion_DensePassRetainsNegativeEvidenceForRecoverySweep()
    {
        _host.RenderDeltaSeconds = 1.0 / 60.0;
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 32;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.5f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 143u;
        AABB bounds = AABB.FromCenterSize(
            camera.Transform.RenderTranslation + camera.Transform.RenderForward * 10.0f,
            Vector3.One);

        BeginPassForPolicyTest(coordinator, camera, sceneCommandCount: 393u);
        object passState = GetPassState(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 20UL);
        SetNonPublicField(queryState, "LastResultFrame", TestFrameId - 20UL);
        SetNonPublicField(queryState, "ResolvedCameraSnapshot", GetNonPublicField(passState, "CurrentCameraSnapshot"));
        SetNonPublicField(queryState, "ResolvedWorldBounds", bounds);
        SetNonPublicField(queryState, "HasResolvedTemporalState", true);

        for (int frame = 1; frame <= 8; frame++)
        {
            _host.CurrentRenderFrameId++;
            camera.Transform.SetRenderMatrix(
                Matrix4x4.CreateTranslation(frame * 0.03f, 0.0f, 0.0f)).GetAwaiter().GetResult();
            BeginPassForPolicyTest(coordinator, camera, sceneCommandCount: 393u);

            ECpuOcclusionDecision decision = coordinator.ShouldRender(
                RenderPass,
                camera,
                queryKey,
                out _,
                default,
                bounds);

            GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.SmallMotion);
            decision.ShouldNotBe(ECpuOcclusionDecision.Visible);
        }

        bool expired = false;
        for (int frame = 9; frame <= 14; frame++)
        {
            _host.CurrentRenderFrameId++;
            camera.Transform.SetRenderMatrix(
                Matrix4x4.CreateTranslation(frame * 0.03f, 0.0f, 0.0f)).GetAwaiter().GetResult();
            BeginPassForPolicyTest(coordinator, camera, sceneCommandCount: 393u);
            expired |= coordinator.ShouldRender(
                RenderPass,
                camera,
                queryKey,
                out _,
                default,
                bounds) == ECpuOcclusionDecision.Visible;
        }

        expired.ShouldBeTrue("negative evidence must still expire if its scheduled refresh never resolves");
    }

    [Test]
    public void BeginPass_MediumMotionPreservesOccludedStateAndRequestsRecoveryProbe()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 44u;

        BeginPassForPolicyTest(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 2UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.5f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);
        GetNonPublicField(queryState, "RecoveryStartedFrame").ShouldBe(ulong.MaxValue);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.MediumMotion);
        decision.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.CameraMotionRevalidation);
        coordinator.PeekShouldRender(RenderPass, camera, queryKey).ShouldBeFalse();
        coordinator.TryGetCurrentDecision(RenderPass, camera, queryKey, default, out ECpuOcclusionDecision replayed)
            .ShouldBeTrue();
        replayed.ShouldBe(ECpuOcclusionDecision.ProbeOnly);
    }

    [Test]
    public void BeginPass_MediumMotionForcesBoundedStaleOcclusionVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 144u;

        BeginPassForPolicyTest(coordinator, camera);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 30UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(0.5f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        GetMotionTier(coordinator, camera).ShouldBe(ECpuOcclusionMotionTier.MediumMotion);
        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeTrue();
        request.RecoveryProbe.ShouldBeFalse();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.StaleStateRefresh);
        coordinator.PeekShouldRender(RenderPass, camera, queryKey).ShouldBeTrue();
    }

    [Test]
    public void PeekShouldRender_ReusesPrimaryDecisionAtOcclusionHysteresisBoundary()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 145u;

        BeginPassForPolicyTest(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId);
        SetNonPublicField(queryState, "ConsecutiveOccludedFrames", 1);

        coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request)
            .ShouldBe(ECpuOcclusionDecision.Visible);

        request.Reason.ShouldBe(ECpuOcclusionQueryReason.VisibleDemotion);
        GetNonPublicField(queryState, "ConsecutiveOccludedFrames").ShouldBe(2);
        coordinator.PeekShouldRender(RenderPass, camera, queryKey).ShouldBeTrue();
    }

    [Test]
    public void StableLargePass_RetainsNegativeEvidenceLongEnoughForTwoBudgetSweeps()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 32;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.5f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 65u;

        BeginPassForPolicyTest(coordinator, camera, sceneCommandCount: 393u);
        SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 20UL);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(
            RenderPass,
            camera,
            queryKey,
            out CpuOcclusionProbeRequest request);

        decision.ShouldBe(ECpuOcclusionDecision.Skip);
        request.Requested.ShouldBeFalse();
        coordinator.PeekShouldRender(RenderPass, camera, queryKey).ShouldBeFalse();
        coordinator.TryGetCurrentDecision(RenderPass, camera, queryKey, default, out ECpuOcclusionDecision replayed)
            .ShouldBeTrue();
        replayed.ShouldBe(ECpuOcclusionDecision.Skip);
    }

    [Test]
    public void TryGetCurrentDecision_MissingStateFailsWithoutCreatingPassState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();

        coordinator.TryGetCurrentDecision(RenderPass, camera, 501u, default, out ECpuOcclusionDecision decision)
            .ShouldBeFalse();

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        ((IDictionary)GetNonPublicField(coordinator, "_passStates")).Count.ShouldBe(0);
    }

    [Test]
    public void BeginPass_LargeMotionDoesNotResetOccludedState()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 45u;

        BeginPassForPolicyTest(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        BeginPassForPolicyTest(coordinator, camera);
        GetNonPublicField(queryState, "RecoveryStartedFrame").ShouldBe(ulong.MaxValue);

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
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(502);

        BeginPassForPolicyTest(coordinator, camera, ownership);
        SeedOccludedQuery(coordinator, camera, ownership, queryKey, TestFrameId - 1UL);

        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(20.0f, 0.0f, 0.0f)).GetAwaiter().GetResult();
        coordinator.BeginPass(RenderPass, camera, sceneCommandCount: 1u, ownership).ShouldBeTrue();

        ECpuOcclusionDecision decision = coordinator.ShouldRender(
            RenderPass,
            camera,
            queryKey,
            out CpuOcclusionProbeRequest request,
            ownership);

        GetMotionTier(coordinator, camera, ownership).ShouldBe(ECpuOcclusionMotionTier.CameraCut);
        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();

        _host.CurrentRenderFrameId++;
        BeginPassForPolicyTest(coordinator, camera, ownership, preserveForcedRecovery: true);

        ECpuOcclusionDecision recoveryDecision = coordinator.ShouldRender(
            RenderPass,
            camera,
            queryKey,
            out CpuOcclusionProbeRequest recoveryRequest,
            ownership);

        recoveryDecision.ShouldBe(ECpuOcclusionDecision.Visible);
        recoveryRequest.Requested.ShouldBeTrue();
        recoveryRequest.Reason.ShouldBe(ECpuOcclusionQueryReason.StaleStateRefresh);
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
        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeTrue();
        request.RecoveryProbe.ShouldBeFalse();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.StaleStateRefresh);
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
    public void CreatePassKey_ExplicitPipelineIdentityIsolatesOtherwiseIdenticalOutputs()
    {
        XRCamera camera = new();

        OcclusionViewKey firstPipeline = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, pipelineInstanceId: 41);
        OcclusionViewKey secondPipeline = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, pipelineInstanceId: 42);

        firstPipeline.Scope.ShouldBe(EOcclusionViewScope.MonoDesktop);
        firstPipeline.PipelineInstanceId.ShouldBe(41);
        secondPipeline.PipelineInstanceId.ShouldBe(42);
        firstPipeline.ShouldNotBe(secondPipeline);
    }

    [Test]
    public void CreatePassKey_OutputIdentityIsolatesTelemetryForOnePipeline()
    {
        XRCamera camera = new();
        OcclusionViewOwnership firstOutput = OcclusionViewOwnership.Independent(43, resourceGeneration: 7, outputId: 0xA001UL);
        OcclusionViewOwnership secondOutput = OcclusionViewOwnership.Independent(43, resourceGeneration: 7, outputId: 0xA002UL);

        OcclusionViewKey firstKey = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, firstOutput);
        OcclusionViewKey secondKey = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, secondOutput);

        firstKey.PipelineInstanceId.ShouldBe(secondKey.PipelineInstanceId);
        firstKey.OutputId.ShouldBe(0xA001UL);
        secondKey.OutputId.ShouldBe(0xA002UL);
        firstKey.ShouldNotBe(secondKey);
    }

    [Test]
    public void BeginPass_DifferentOutputDoesNotReuseNegativeHistory()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 186u;
        OcclusionViewOwnership firstOutput = OcclusionViewOwnership.Independent(44, outputId: 0xA101UL);
        OcclusionViewOwnership secondOutput = OcclusionViewOwnership.Independent(44, outputId: 0xA102UL);

        BeginPassForPolicyTest(coordinator, camera, firstOutput);
        SeedOccludedQuery(coordinator, camera, firstOutput, queryKey, TestFrameId - 1UL);

        _host.CurrentRenderFrameId++;
        BeginPassForPolicyTest(coordinator, camera, secondOutput);
        coordinator.ShouldRender(
                RenderPass,
                camera,
                queryKey,
                out CpuOcclusionProbeRequest request,
                secondOutput)
            .ShouldBe(ECpuOcclusionDecision.Visible);

        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.InitialSeed);
    }

    [Test]
    public void TrueSinglePassStereoScopeSupportsConservativeMultiviewQueriesWithoutPairSharingMode()
    {
        _host.CpuQueryStereoMode = ECpuQueryStereoMode.PerEyeSequential;

        CpuRenderOcclusionCoordinator.IsUnsupportedSharedStereoScope(
            new OcclusionViewKey(RenderPass, EOcclusionViewScope.VrSinglePassStereo, pipelineInstanceId: 8)).ShouldBeFalse();
        CpuRenderOcclusionCoordinator.IsUnsupportedSharedStereoScope(
            new OcclusionViewKey(RenderPass, EOcclusionViewScope.VrFoveatedView, pipelineInstanceId: 8)).ShouldBeFalse();
        CpuRenderOcclusionCoordinator.IsUnsupportedSharedStereoScope(
            new OcclusionViewKey(RenderPass, EOcclusionViewScope.VrStereoPair, pipelineInstanceId: 8)).ShouldBeTrue();

        OcclusionViewOwnership explicitLeft = StereoOwnership(9, -9, 0x1u, 0x3u, 2);
        CpuRenderOcclusionCoordinator.IsUnsupportedSharedStereoScope(
            CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, CreateEyeCamera(leftEye: true), explicitLeft)).ShouldBeFalse();
    }

    [Test]
    public void SequentialEyesInOnePov_VisibilityInEitherEyeKeepsBothVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera leftCamera = CreateEyeCamera(leftEye: true);
        XRCamera rightCamera = CreateEyeCamera(leftEye: false);
        const uint queryKey = 181u;
        const int povId = -51;
        OcclusionViewOwnership left = StereoOwnership(101, povId, 0x1u, 0x3u, 2);
        OcclusionViewOwnership right = StereoOwnership(102, povId, 0x2u, 0x3u, 2);

        BeginPassForPolicyTest(coordinator, leftCamera, left);
        BeginPassForPolicyTest(coordinator, rightCamera, right);
        SeedOccludedQuery(coordinator, leftCamera, left, queryKey, TestFrameId - 1UL);
        SeedOccludedQuery(coordinator, rightCamera, right, queryKey, TestFrameId - 1UL);
        SeedPovResult(coordinator, leftCamera, left, queryKey, anySamplesPassed: false);
        SeedPovResult(coordinator, rightCamera, right, queryKey, anySamplesPassed: true);

        coordinator.ShouldRender(RenderPass, leftCamera, queryKey, out _, left)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        coordinator.ShouldRender(RenderPass, rightCamera, queryKey, out _, right)
            .ShouldBe(ECpuOcclusionDecision.Visible);
    }

    [Test]
    public void SequentialEyesInOnePov_CullOnlyAfterBothEyesAreOccluded()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera leftCamera = CreateEyeCamera(leftEye: true);
        XRCamera rightCamera = CreateEyeCamera(leftEye: false);
        const uint queryKey = 182u;
        const int povId = -52;
        OcclusionViewOwnership left = StereoOwnership(111, povId, 0x1u, 0x3u, 2);
        OcclusionViewOwnership right = StereoOwnership(112, povId, 0x2u, 0x3u, 2);

        BeginPassForPolicyTest(coordinator, leftCamera, left);
        BeginPassForPolicyTest(coordinator, rightCamera, right);
        SeedOccludedQuery(coordinator, leftCamera, left, queryKey, TestFrameId - 1UL);
        SeedOccludedQuery(coordinator, rightCamera, right, queryKey, TestFrameId - 1UL);
        SeedPovResult(coordinator, leftCamera, left, queryKey, anySamplesPassed: false);
        SeedPovResult(coordinator, rightCamera, right, queryKey, anySamplesPassed: false);

        coordinator.ShouldRender(RenderPass, leftCamera, queryKey, out _, left)
            .ShouldBe(ECpuOcclusionDecision.Skip);
        coordinator.ShouldRender(RenderPass, rightCamera, queryKey, out _, right)
            .ShouldBe(ECpuOcclusionDecision.Skip);
    }

    [Test]
    public void TrueSinglePassStereo_OnePhysicalQueryCoversBothViews()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera leftCamera = CreateEyeCamera(leftEye: true);
        const uint queryKey = 183u;
        var ownership = new OcclusionViewOwnership(
            pipelineInstanceId: 121,
            povId: -53,
            EOcclusionViewScope.VrSinglePassStereo,
            coverageMask: 0x3u,
            requiredCoverageMask: 0x3u,
            declaredViewCount: 2,
            resourceGeneration: 7);

        BeginPassForPolicyTest(coordinator, leftCamera, ownership);
        SeedOccludedQuery(coordinator, leftCamera, ownership, queryKey, TestFrameId - 1UL);
        SeedPovResult(coordinator, leftCamera, ownership, queryKey, anySamplesPassed: false);

        coordinator.ShouldRender(RenderPass, leftCamera, queryKey, out _, ownership)
            .ShouldBe(ECpuOcclusionDecision.Skip);
        coordinator.GetOccludedProofCoverageMask(RenderPass, leftCamera, queryKey, ownership)
            .ShouldBe(0x3u);
        OcclusionViewKey key = CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftCamera, ownership);
        key.CoverageMask.ShouldBe(0x3u);
        key.RequiredCoverageMask.ShouldBe(0x3u);
    }

    [Test]
    public void QuadViewPov_RequiresAllFourCoverageBitsAndKeepsDesktopIndependent()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera leftCamera = CreateEyeCamera(leftEye: true);
        XRCamera rightCamera = CreateEyeCamera(leftEye: false);
        XRCamera desktopCamera = new();
        const uint queryKey = 184u;
        const int povId = -54;
        OcclusionViewOwnership left = StereoOwnership(131, povId, 0x5u, 0xFu, 4, EOcclusionViewScope.VrFoveatedView);
        OcclusionViewOwnership right = StereoOwnership(132, povId, 0xAu, 0xFu, 4, EOcclusionViewScope.VrFoveatedView);
        OcclusionViewOwnership desktop = OcclusionViewOwnership.Independent(133);

        BeginPassForPolicyTest(coordinator, leftCamera, left);
        BeginPassForPolicyTest(coordinator, rightCamera, right);
        BeginPassForPolicyTest(coordinator, desktopCamera, desktop);
        SeedOccludedQuery(coordinator, leftCamera, left, queryKey, TestFrameId - 1UL);
        SeedOccludedQuery(coordinator, rightCamera, right, queryKey, TestFrameId - 1UL);
        SeedOccludedQuery(coordinator, desktopCamera, desktop, queryKey, TestFrameId - 1UL);
        SeedPovResult(coordinator, leftCamera, left, queryKey, anySamplesPassed: false);
        SeedPovResult(coordinator, rightCamera, right, queryKey, anySamplesPassed: true);

        coordinator.ShouldRender(RenderPass, leftCamera, queryKey, out _, left)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        coordinator.ShouldRender(RenderPass, rightCamera, queryKey, out _, right)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        coordinator.ShouldRender(RenderPass, desktopCamera, queryKey, out _, desktop)
            .ShouldBe(ECpuOcclusionDecision.Skip);

        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, leftCamera, left).RequiredCoverageMask.ShouldBe(0xFu);
        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, rightCamera, right).CoverageMask.ShouldBe(0xAu);
    }

    [Test]
    public void SharedPov_MissingOrStaleCoverageFailsVisibleAndRequestsReprobe()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera leftCamera = CreateEyeCamera(leftEye: true);
        const uint queryKey = 185u;
        OcclusionViewOwnership left = StereoOwnership(141, -55, 0x1u, 0x3u, 2);

        BeginPassForPolicyTest(coordinator, leftCamera, left);
        SeedOccludedQuery(coordinator, leftCamera, left, queryKey, TestFrameId - 30UL);
        SeedPovResult(coordinator, leftCamera, left, queryKey, anySamplesPassed: false);

        ECpuOcclusionDecision decision = coordinator.ShouldRender(
            RenderPass,
            leftCamera,
            queryKey,
            out CpuOcclusionProbeRequest request,
            left);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.StaleStateRefresh);
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
    public void BeginPass_CommandSetChangeFailsVisibleAndRequiresReseed()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint existingKey = 49u;
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(301);

        coordinator.BeginPass(RenderPass, camera, 1u, 0x1111UL, ownership);
        object passState = GetPassState(coordinator, camera, ownership);
        SetNonPublicField(passState, "ForceVisibleThisFrame", false);
        SetNonPublicField(passState, "ForceVisibleReason", ECpuOcclusionForceVisibleReason.None);
        object queryState = SeedOccludedQuery(coordinator, camera, ownership, existingKey, TestFrameId - 1UL);

        coordinator.BeginPass(RenderPass, camera, 2u, 0x2222UL, ownership);
        ECpuOcclusionDecision changedDecision = coordinator.ShouldRender(
            RenderPass,
            camera,
            existingKey,
            out CpuOcclusionProbeRequest changedRequest,
            ownership);

        changedDecision.ShouldBe(ECpuOcclusionDecision.Visible);
        changedRequest.Requested.ShouldBeFalse();
        GetNonPublicField(passState, "ForceVisibleReason")
            .ShouldBe(ECpuOcclusionForceVisibleReason.CommandSetChanged);
        GetNonPublicField(queryState, "RecoveryStartedFrame")
            .ShouldBe(GetPassFrameEpoch(passState));

        _host.CurrentRenderFrameId++;
        coordinator.BeginPass(RenderPass, camera, 2u, 0x2222UL, ownership);
        SetNonPublicField(passState, "ForceVisibleThisFrame", false);
        SetNonPublicField(passState, "ForceVisibleReason", ECpuOcclusionForceVisibleReason.None);
        ECpuOcclusionDecision reseedDecision = coordinator.ShouldRender(
            RenderPass,
            camera,
            existingKey,
            out CpuOcclusionProbeRequest reseedRequest,
            ownership);

        reseedDecision.ShouldBe(ECpuOcclusionDecision.Visible);
        reseedRequest.Requested.ShouldBeTrue();
        reseedRequest.Reason.ShouldBe(ECpuOcclusionQueryReason.InitialSeed);
    }

    [Test]
    public void BeginPass_SameCountReplacementSignatureInvalidatesNegativeHistory()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 51u;
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(302);

        coordinator.BeginPass(RenderPass, camera, 2u, 0xAAAAUL, ownership);
        object passState = GetPassState(coordinator, camera, ownership);
        SetNonPublicField(passState, "ForceVisibleThisFrame", false);
        SeedOccludedQuery(coordinator, camera, ownership, queryKey, TestFrameId - 1UL);

        coordinator.BeginPass(RenderPass, camera, 2u, 0xBBBBUL, ownership);

        coordinator.ShouldRender(RenderPass, camera, queryKey, out _, ownership)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        GetNonPublicField(passState, "ForceVisibleReason")
            .ShouldBe(ECpuOcclusionForceVisibleReason.CommandSetChanged);
    }

    [Test]
    public void BeginPass_MissingOwnershipFailsVisibleThenValidOwnershipSeedsProbe()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 52u;

        coordinator.BeginPass(RenderPass, camera, sceneCommandCount: 1u, ownership: default);
        coordinator.ShouldRender(
                RenderPass,
                camera,
                queryKey,
                out CpuOcclusionProbeRequest missingRequest,
                default(OcclusionViewOwnership))
            .ShouldBe(ECpuOcclusionDecision.Visible);
        missingRequest.Requested.ShouldBeFalse();

        _host.CurrentRenderFrameId++;
        OcclusionViewOwnership validOwnership = OcclusionViewOwnership.Independent(401, outputId: 0xB001UL);
        BeginPassForPolicyTest(coordinator, camera, validOwnership);

        coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest seedRequest, validOwnership)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        seedRequest.Requested.ShouldBeTrue();
        seedRequest.Reason.ShouldBe(ECpuOcclusionQueryReason.InitialSeed);
    }

    [Test]
    public void BeginPass_ResourceGenerationChangeDoesNotReuseNegativeHistory()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 53u;
        OcclusionViewOwnership initialOwnership = OcclusionViewOwnership.Independent(
            402,
            resourceGeneration: 11,
            outputId: 0xB002UL);
        OcclusionViewOwnership recreatedExtentOwnership = initialOwnership.WithResourceGeneration(12);

        BeginPassForPolicyTest(coordinator, camera, initialOwnership);
        SeedOccludedQuery(coordinator, camera, initialOwnership, queryKey, TestFrameId - 1UL);

        _host.CurrentRenderFrameId++;
        BeginPassForPolicyTest(coordinator, camera, recreatedExtentOwnership);
        coordinator.ShouldRender(
                RenderPass,
                camera,
                queryKey,
                out CpuOcclusionProbeRequest request,
                recreatedExtentOwnership)
            .ShouldBe(ECpuOcclusionDecision.Visible);

        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.InitialSeed);
        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, initialOwnership)
            .ShouldNotBe(CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, recreatedExtentOwnership));
    }

    [Test]
    public void BeginPass_PipelineRecreationDoesNotReuseNegativeHistory()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 54u;
        OcclusionViewOwnership initialOwnership = OcclusionViewOwnership.Independent(403, outputId: 0xB003UL);
        OcclusionViewOwnership recreatedPipelineOwnership = OcclusionViewOwnership.Independent(404, outputId: 0xB003UL);

        BeginPassForPolicyTest(coordinator, camera, initialOwnership);
        SeedOccludedQuery(coordinator, camera, initialOwnership, queryKey, TestFrameId - 1UL);

        _host.CurrentRenderFrameId++;
        BeginPassForPolicyTest(coordinator, camera, recreatedPipelineOwnership);
        coordinator.ShouldRender(
                RenderPass,
                camera,
                queryKey,
                out CpuOcclusionProbeRequest request,
                recreatedPipelineOwnership)
            .ShouldBe(ECpuOcclusionDecision.Visible);

        request.Requested.ShouldBeTrue();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.InitialSeed);
        CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, initialOwnership)
            .ShouldNotBe(CpuRenderOcclusionCoordinator.CreatePassKey(RenderPass, camera, recreatedPipelineOwnership));
    }

    [Test]
    public void OcclusionTelemetry_ReportsAllCountersPerFullOutputKey()
    {
        var key = new OcclusionViewKey(
            RenderPass,
            EOcclusionViewScope.VrSinglePassStereo,
            viewId: -1,
            pipelineInstanceId: 405,
            povId: -405,
            coverageMask: 0x3u,
            requiredCoverageMask: 0x3u,
            declaredViewCount: 2,
            resourceGeneration: 13,
            outputId: 0xB004UL);

        OcclusionTelemetry.BeginFrame();
        OcclusionTelemetry.RecordCpuViewPassBegin(key, candidateCount: 7);
        OcclusionTelemetry.RecordCpuViewSubmission(key);
        OcclusionTelemetry.RecordCpuViewResolution(key, submittedFrame: TestFrameId - 4UL, resolvedFrame: TestFrameId);
        OcclusionTelemetry.RecordCpuViewSkip(key);
        OcclusionTelemetry.RecordCpuViewBudgetSkipped(key, count: 2);
        OcclusionTelemetry.RecordCpuViewForcedVisible(key);
        OcclusionTelemetry.RecordCpuViewResultAge(key, ageFrames: 3);
        var telemetry = OcclusionTelemetry.GetCpuViewTelemetryHandle(key);
        telemetry.RecordRecoveryStarted();
        telemetry.RecordRecoveryAge(ageFrames: 4);
        telemetry.RecordRecoveryCompleted(TestFrameId - 4UL, TestFrameId);
        OcclusionTelemetry.BeginFrame();

        CpuOcclusionViewTelemetrySnapshot snapshot = OcclusionTelemetry.GetCpuViewSnapshots()
            .Single(value => value.ViewKey.Equals(key));
        snapshot.ViewKey.OutputId.ShouldBe(0xB004UL);
        snapshot.CandidateCount.ShouldBe(7);
        snapshot.Submissions.ShouldBe(1);
        snapshot.Resolutions.ShouldBe(1);
        snapshot.Skips.ShouldBe(1);
        snapshot.BudgetSkipped.ShouldBe(2);
        snapshot.ForcedVisible.ShouldBe(1);
        snapshot.RecoveryStarts.ShouldBe(1);
        snapshot.RecoveryCompletions.ShouldBe(1);
        snapshot.CurrentRecoveryAgeFrames.ShouldBe(4);
        snapshot.MaxRecoveryAgeFrames.ShouldBe(4);
        snapshot.CurrentResultAgeFrames.ShouldBe(3);
        snapshot.MaxResultAgeFrames.ShouldBe(3);
        snapshot.RecoveryLatencyFrames.ShouldBe(4);

        OcclusionTelemetry.GetCpuViewTelemetryHandle(default).RecordSubmission();
        var activeSnapshots = new CpuOcclusionViewTelemetrySnapshot[
            OcclusionTelemetry.CpuActiveViewSnapshotCount];
        int activeCount = OcclusionTelemetry.CopyLastActiveCpuViewSnapshots(activeSnapshots);
        activeSnapshots.AsSpan(0, activeCount).ToArray()
            .Any(value => value.ViewKey.Equals(key))
            .ShouldBeTrue();
        activeSnapshots.AsSpan(0, activeCount).ToArray()
            .All(value => value.ViewKey.IsValid)
            .ShouldBeTrue();

        OcclusionTelemetry.BeginFrame();
        OcclusionTelemetry.GetCpuViewSnapshots()
            .Any(value => value.ViewKey.Equals(key))
            .ShouldBeFalse();
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
    public void SelectProbeCandidates_RespectsVisibleBudget()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 4;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.25f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(505);
        BeginPassForPolicyTest(coordinator, camera, ownership, sceneCommandCount: 2u);
        CpuOcclusionProbeRequest request = new(
            requested: true,
            ECpuOcclusionQueryReason.StaleStateRefresh,
            recoveryProbe: false);

        coordinator.ShouldRender(RenderPass, camera, 64u, out _, ownership);
        coordinator.ShouldRender(RenderPass, camera, 65u, out _, ownership);
        List<CpuOcclusionProbeCandidate> candidates =
        [
            new(64u, UnitBounds(), request, screenPriority: 2.0f, distanceMeters: 1.0f),
            new(65u, UnitBounds(), request, screenPriority: 1.0f, distanceMeters: 2.0f),
        ];
        List<CpuOcclusionScheduledProbe> scheduled = [];

        coordinator.SelectProbeCandidates(RenderPass, camera, candidates, scheduled, ownership);

        scheduled.Count.ShouldBe(1);
        scheduled[0].QueryKey.ShouldBe(64u);
    }

    [Test]
    public void SelectVisibleDrawCandidates_GatesNextPassByGlobalPriority()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 4;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.25f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(509);
        BeginPassForPolicyTest(coordinator, camera, ownership, sceneCommandCount: 2u);

        coordinator.ShouldRender(RenderPass, camera, 64u, out CpuOcclusionProbeRequest lowRequest, ownership);
        coordinator.ShouldRender(RenderPass, camera, 65u, out CpuOcclusionProbeRequest highRequest, ownership);
        List<CpuOcclusionProbeCandidate> candidates =
        [
            new(64u, UnitBounds(), lowRequest, screenPriority: 1.0f, distanceMeters: 2.0f),
            new(65u, UnitBounds(), highRequest, screenPriority: 2.0f, distanceMeters: 1.0f),
        ];

        coordinator.SelectVisibleDrawCandidates(RenderPass, camera, candidates, ownership);

        coordinator.TryScheduleVisibleDrawQuery(
            RenderPass,
            camera,
            64u,
            lowRequest,
            ownership).ShouldBeFalse();
        coordinator.TryScheduleVisibleDrawQuery(
            RenderPass,
            camera,
            65u,
            highRequest,
            ownership).ShouldBeTrue();
    }

    [Test]
    public void SelectVisibleDrawCandidates_RequestTierOutranksProjectedArea()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 4;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.25f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(510);
        BeginPassForPolicyTest(coordinator, camera, ownership, sceneCommandCount: 2u);

        coordinator.ShouldRender(RenderPass, camera, 64u, out _, ownership);
        coordinator.ShouldRender(RenderPass, camera, 65u, out _, ownership);
        CpuOcclusionProbeRequest refreshRequest = new(
            true,
            ECpuOcclusionQueryReason.VisibleDemotion,
            recoveryProbe: false,
            priorityBias: 0.0f);
        CpuOcclusionProbeRequest seedRequest = new(
            true,
            ECpuOcclusionQueryReason.InitialSeed,
            recoveryProbe: false,
            priorityBias: 2.0f);
        List<CpuOcclusionProbeCandidate> candidates =
        [
            new(64u, UnitBounds(), refreshRequest, screenPriority: 1000.0f, distanceMeters: 1.0f),
            new(65u, UnitBounds(), seedRequest, screenPriority: 1.0f, distanceMeters: 100.0f),
        ];

        coordinator.SelectVisibleDrawCandidates(RenderPass, camera, candidates, ownership);

        coordinator.TryScheduleVisibleDrawQuery(RenderPass, camera, 64u, refreshRequest, ownership).ShouldBeFalse();
        coordinator.TryScheduleVisibleDrawQuery(RenderPass, camera, 65u, seedRequest, ownership).ShouldBeTrue();
    }

    [Test]
    public void TryScheduleVisibleDrawQuery_RespectsTotalPendingQueryCap()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 2;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 1.0f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(506);
        BeginPassForPolicyTest(coordinator, camera, ownership, sceneCommandCount: 3u);

        object firstPending = SeedOccludedQuery(
            coordinator,
            camera,
            ownership,
            queryKey: 64u,
            lastQueryFrame: TestFrameId - 1UL);
        object secondPending = SeedOccludedQuery(
            coordinator,
            camera,
            ownership,
            queryKey: 65u,
            lastQueryFrame: TestFrameId - 1UL);
        SetNonPublicField(firstPending, "QueryPending", true);
        SetNonPublicField(secondPending, "QueryPending", true);

        bool scheduled = coordinator.TryScheduleVisibleDrawQuery(
            RenderPass,
            camera,
            sourceCommandIndex: 66u,
            request: new CpuOcclusionProbeRequest(
                requested: true,
                ECpuOcclusionQueryReason.VisibleDemotion,
                recoveryProbe: false),
            ownership: ownership);

        scheduled.ShouldBeFalse();
    }

    [Test]
    public void TryScheduleVisibleDrawQuery_CountsSameFrameReservationsAgainstPendingCap()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 2;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 1.0f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(507);
        BeginPassForPolicyTest(coordinator, camera, ownership, sceneCommandCount: 3u);

        object pending = SeedOccludedQuery(
            coordinator,
            camera,
            ownership,
            queryKey: 64u,
            lastQueryFrame: TestFrameId - 1UL);
        SetNonPublicField(pending, "QueryPending", true);

        CpuOcclusionProbeRequest request = new(
            requested: true,
            ECpuOcclusionQueryReason.VisibleDemotion,
            recoveryProbe: false);

        coordinator.TryScheduleVisibleDrawQuery(
            RenderPass,
            camera,
            sourceCommandIndex: 65u,
            request: request,
            ownership: ownership).ShouldBeTrue();
        coordinator.TryScheduleVisibleDrawQuery(
            RenderPass,
            camera,
            sourceCommandIndex: 66u,
            request: request,
            ownership: ownership).ShouldBeFalse();
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
    public void SelectProbeCandidates_VisibleHierarchyResultExpandsToIndividualProbes()
    {
        _host.CpuQueryOcclusionMaxQueriesPerFrame = 4;
        _host.CpuQueryOcclusionVisibleDemotionBudgetFraction = 0.25f;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        BeginPassForPolicyTest(coordinator, camera);

        object passState = GetPassState(coordinator, camera);
        object group = InvokeNonPublic(coordinator, "GetOrCreateHierarchyGroup", passState, 2u)
            .ShouldNotBeNull();
        SetNonPublicField(group, "LastQueryFrame", TestFrameId - 1UL);
        SetNonPublicField(group, "LastAnySamplesPassed", true);

        List<CpuOcclusionProbeCandidate> candidates = [];
        for (uint key = 64u; key < 68u; key++)
        {
            SeedOccludedQuery(coordinator, camera, key, TestFrameId - 8UL);
            candidates.Add(new CpuOcclusionProbeCandidate(
                key,
                UnitBounds(),
                new CpuOcclusionProbeRequest(
                    true,
                    ECpuOcclusionQueryReason.OccludedRecovery,
                    recoveryProbe: true),
                screenPriority: key,
                distanceMeters: 1.0f));
        }

        List<CpuOcclusionScheduledProbe> scheduled = [];
        coordinator.SelectProbeCandidates(RenderPass, camera, candidates, scheduled);

        scheduled.ShouldNotBeEmpty();
        scheduled.ShouldAllBe(probe => !probe.IsHierarchyGroup);
    }

    [Test]
    public void ShouldRender_OverduePendingQueryForcesVisibleAndReprobesNextFrame()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 78u;
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(503);

        BeginPassForPolicyTest(coordinator, camera, ownership);
        object queryState = SeedOccludedQuery(
            coordinator,
            camera,
            ownership,
            queryKey,
            TestFrameId - 8UL);
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingSinceFrame", 1UL);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", false);
        XRRenderQuery expiredQuery = (XRRenderQuery)GetNonPublicField(queryState, "Query");

        ECpuOcclusionDecision decision = coordinator.ShouldRender(
            RenderPass,
            camera,
            queryKey,
            out CpuOcclusionProbeRequest request,
            ownership);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();
        GetNonPublicField(queryState, "QueryPending").ShouldBe(false);
        GetNonPublicField(queryState, "StateKind").ShouldBe(ECpuOcclusionQueryStateKind.ForcedVisible);
        GetNonPublicField(queryState, "Query").ShouldNotBeSameAs(expiredQuery);

        _host.CurrentRenderFrameId++;
        BeginPassForPolicyTest(coordinator, camera, ownership, preserveForcedRecovery: true);
        coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest recoveryRequest, ownership)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        recoveryRequest.Requested.ShouldBeTrue();
        recoveryRequest.Reason.ShouldBe(ECpuOcclusionQueryReason.StaleStateRefresh);
    }

    [Test]
    public void BeginPass_OverdueHierarchyQueryRetiresEpochAndFailsVisible()
    {
        using IDisposable _ = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        _host.CpuQueryOcclusionMaxPendingFrames = 1;

        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(508);
        BeginPassForPolicyTest(coordinator, camera, ownership);

        object passState = GetPassState(coordinator, camera, ownership);
        object group = InvokeNonPublic(coordinator, "GetOrCreateHierarchyGroup", passState, 2u)
            .ShouldNotBeNull();
        object queryState = GetNonPublicField(group, "Query");
        XRRenderQuery expiredQuery = (XRRenderQuery)GetNonPublicField(queryState, "Query");
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingSinceFrame", 0UL);
        SetNonPublicField(group, "LastAnySamplesPassed", false);

        _host.CurrentRenderFrameId++;
        BeginPassForPolicyTest(coordinator, camera, ownership);

        GetNonPublicField(queryState, "QueryPending").ShouldBe(false);
        GetNonPublicField(queryState, "Query").ShouldNotBeSameAs(expiredQuery);
        GetNonPublicField(queryState, "StateKind").ShouldBe(ECpuOcclusionQueryStateKind.ForcedVisible);
        GetNonPublicField(group, "LastAnySamplesPassed").ShouldBe(true);
    }

    [Test]
    public void PendingMonoRecoveryProbeRetainsTheNegativeResultUsedBySkipDecision()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 178u;
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(504);

        BeginPassForPolicyTest(coordinator, camera, ownership);
        object queryState = SeedOccludedQuery(
            coordinator,
            camera,
            ownership,
            queryKey,
            TestFrameId - 1UL);
        SetNonPublicField(queryState, "QueryPending", true);
        SetNonPublicField(queryState, "PendingSinceFrame", TestFrameId);
        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", false);

        coordinator.ShouldRender(RenderPass, camera, queryKey, out _, ownership)
            .ShouldBe(ECpuOcclusionDecision.Skip);
        coordinator.GetOccludedProofCoverageMask(RenderPass, camera, queryKey, ownership)
            .ShouldBe(0x1u);

        SetNonPublicField(queryState, "PendingQueryWasVisibleDraw", true);
        coordinator.GetOccludedProofCoverageMask(RenderPass, camera, queryKey, ownership)
            .ShouldBe(0u);
    }

    [Test]
    public void ForceVisible_NearPlaneUnsafeKeepsCommandVisible()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 79u;

        BeginPassForPolicyTest(coordinator, camera);
        object queryState = SeedOccludedQuery(coordinator, camera, queryKey, TestFrameId - 1UL);
        SetNonPublicField(queryState, "RecoveryStartedFrame", TestFrameId - 32UL);

        coordinator.ForceVisible(RenderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NearPlaneUnsafe);
        ECpuOcclusionDecision decision = coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest request);

        decision.ShouldBe(ECpuOcclusionDecision.Visible);
        request.Requested.ShouldBeFalse();
        GetNonPublicField(queryState, "RecoveryStartedFrame").ShouldBe(ulong.MaxValue);
    }

    [Test]
    public void ForceVisibleForValidation_RequestsIndividualProbeWithoutCulling()
    {
        CpuRenderOcclusionCoordinator coordinator = new();
        XRCamera camera = new();
        const uint queryKey = 81u;
        OcclusionViewOwnership ownership = OcclusionViewOwnership.Independent(501);

        BeginPassForPolicyTest(coordinator, camera, ownership);
        object queryState = SeedOccludedQuery(
            coordinator,
            camera,
            ownership,
            queryKey,
            TestFrameId - 1UL);

        CpuOcclusionProbeRequest request = coordinator.ForceVisibleForValidation(
            RenderPass,
            camera,
            queryKey,
            ownership);

        request.Requested.ShouldBeTrue();
        request.RecoveryProbe.ShouldBeFalse();
        request.Reason.ShouldBe(ECpuOcclusionQueryReason.DiagnosticForcedQuery);
        coordinator.ShouldRender(RenderPass, camera, queryKey, out CpuOcclusionProbeRequest cachedRequest, ownership)
            .ShouldBe(ECpuOcclusionDecision.Visible);
        cachedRequest.Requested.ShouldBeTrue();
        coordinator.PeekShouldRender(RenderPass, camera, queryKey, ownership).ShouldBeTrue();
        GetNonPublicField(queryState, "RecoveryStartedFrame")
            .ShouldBe(GetPassFrameEpoch(GetPassState(coordinator, camera, ownership)));
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
        uint sceneCommandCount = 1u,
        bool preserveForcedRecovery = false)
    {
        Dictionary<object, ulong>? recoveryStarts = preserveForcedRecovery
            ? CaptureRecoveryStarts(GetPassState(coordinator, camera))
            : null;
        coordinator.BeginPass(RenderPass, camera, sceneCommandCount);
        object passState = GetPassState(coordinator, camera);
        RestoreRecoveryStarts(recoveryStarts);
        SetNonPublicField(passState, "ForceVisibleThisFrame", false);
        SetNonPublicField(passState, "ForceVisibleReason", ECpuOcclusionForceVisibleReason.None);
        ClearPolicyTestRecovery(passState, clearRecovery: !preserveForcedRecovery);
    }

    private static void BeginPassForPolicyTest(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        OcclusionViewOwnership ownership,
        uint sceneCommandCount = 1u,
        bool preserveForcedRecovery = false)
    {
        Dictionary<object, ulong>? recoveryStarts = preserveForcedRecovery
            ? CaptureRecoveryStarts(GetPassState(coordinator, camera, ownership))
            : null;
        coordinator.BeginPass(RenderPass, camera, sceneCommandCount, ownership);
        object passState = GetPassState(coordinator, camera, ownership);
        RestoreRecoveryStarts(recoveryStarts);
        SetNonPublicField(passState, "ForceVisibleThisFrame", false);
        SetNonPublicField(passState, "ForceVisibleReason", ECpuOcclusionForceVisibleReason.None);
        ClearPolicyTestRecovery(passState, clearRecovery: !preserveForcedRecovery);
    }

    private static Dictionary<object, ulong> CaptureRecoveryStarts(object passState)
    {
        Dictionary<object, ulong> recoveryStarts = [];
        IDictionary queries = (IDictionary)GetNonPublicField(passState, "Queries");
        foreach (object queryState in queries.Values)
        {
            ulong recoveryStartedFrame = (ulong)GetNonPublicField(queryState, "RecoveryStartedFrame");
            if (recoveryStartedFrame != ulong.MaxValue)
                recoveryStarts.Add(queryState, recoveryStartedFrame);
        }
        return recoveryStarts;
    }

    private static void RestoreRecoveryStarts(Dictionary<object, ulong>? recoveryStarts)
    {
        if (recoveryStarts is null)
            return;

        foreach ((object queryState, ulong recoveryStartedFrame) in recoveryStarts)
            SetNonPublicField(queryState, "RecoveryStartedFrame", recoveryStartedFrame);
    }

    private static void ClearPolicyTestRecovery(object passState, bool clearRecovery)
    {
        if (!clearRecovery)
            return;

        IDictionary queries = (IDictionary)GetNonPublicField(passState, "Queries");
        foreach (object queryState in queries.Values)
            SetNonPublicField(queryState, "RecoveryStartedFrame", ulong.MaxValue);
    }

    private static object SeedOccludedQuery(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        uint queryKey,
        ulong lastQueryFrame)
    {
        object passState = GetPassState(coordinator, camera);
        object queryState = InvokeNonPublic(coordinator, "GetOrCreateQueryState", passState, queryKey).ShouldNotBeNull();
        ulong frameId = NormalizePassFrameEpoch(passState);

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

    private static object SeedOccludedQuery(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        OcclusionViewOwnership ownership,
        uint queryKey,
        ulong lastQueryFrame)
    {
        object passState = GetPassState(coordinator, camera, ownership);
        return SeedOccludedQueryState(coordinator, passState, queryKey, lastQueryFrame);
    }

    private static object SeedOccludedQueryState(
        CpuRenderOcclusionCoordinator coordinator,
        object passState,
        uint queryKey,
        ulong lastQueryFrame)
    {
        object queryState = InvokeNonPublic(coordinator, "GetOrCreateQueryState", passState, queryKey).ShouldNotBeNull();
        ulong frameId = NormalizePassFrameEpoch(passState);

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

    private static void SeedPovResult(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        OcclusionViewOwnership ownership,
        uint queryKey,
        bool anySamplesPassed)
    {
        object passState = GetPassState(coordinator, camera, ownership);
        InvokeNonPublic(
            coordinator,
            "ApplyResolvedPovResult",
            passState,
            queryKey,
            GetPassFrameEpoch(passState),
            anySamplesPassed);
    }

    private static ulong NormalizePassFrameEpoch(object passState)
    {
        ulong frameId = GetPassFrameEpoch(passState);
        if (frameId >= TestFrameId)
            return frameId;

        SetNonPublicField(passState, "FrameEpoch", TestFrameId);
        return TestFrameId;
    }

    private static ulong GetPassFrameEpoch(object passState)
        => (ulong)GetNonPublicField(passState, "FrameEpoch");

    private static ECpuOcclusionMotionTier GetMotionTier(CpuRenderOcclusionCoordinator coordinator, XRCamera camera)
        => (ECpuOcclusionMotionTier)GetNonPublicField(GetPassState(coordinator, camera), "MotionTier");

    private static ECpuOcclusionMotionTier GetMotionTier(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        OcclusionViewOwnership ownership)
        => (ECpuOcclusionMotionTier)GetNonPublicField(
            GetPassState(coordinator, camera, ownership),
            "MotionTier");

    private static object GetPassState(CpuRenderOcclusionCoordinator coordinator, XRCamera camera)
        => InvokeNonPublic(coordinator, "GetPassState", RenderPass, camera).ShouldNotBeNull();

    private static object GetPassState(
        CpuRenderOcclusionCoordinator coordinator,
        XRCamera camera,
        OcclusionViewOwnership ownership)
        => InvokeNonPublic(coordinator, "GetPassStateForOwnership", RenderPass, camera, ownership).ShouldNotBeNull();

    private static OcclusionViewOwnership StereoOwnership(
        int pipelineInstanceId,
        int povId,
        uint coverageMask,
        uint requiredCoverageMask,
        int declaredViewCount,
        EOcclusionViewScope scope = EOcclusionViewScope.VrStereoPair)
        => new(
            pipelineInstanceId,
            povId,
            scope,
            coverageMask,
            requiredCoverageMask,
            declaredViewCount,
            resourceGeneration: 3);

    private static object? InvokeNonPublic(object target, string methodName, params object?[] args)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
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
        public int? CpuQueryOcclusionMaxPendingFrames { get; set; }
        public float? CpuQueryOcclusionVisibleDemotionBudgetFraction { get; set; }
        public int? CpuQueryOcclusionRecoveryMinCadenceFrames { get; set; }
        public double? RenderDeltaSeconds { get; set; }

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
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionMaxPendingFrames)}" && CpuQueryOcclusionMaxPendingFrames.HasValue)
                return CpuQueryOcclusionMaxPendingFrames.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionVisibleDemotionBudgetFraction)}" && CpuQueryOcclusionVisibleDemotionBudgetFraction.HasValue)
                return CpuQueryOcclusionVisibleDemotionBudgetFraction.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.CpuQueryOcclusionRecoveryMinCadenceFrames)}" && CpuQueryOcclusionRecoveryMinCadenceFrames.HasValue)
                return CpuQueryOcclusionRecoveryMinCadenceFrames.Value;
            if (methodName == $"get_{nameof(IRuntimeRenderingHostServices.RenderDeltaSeconds)}" && RenderDeltaSeconds.HasValue)
                return RenderDeltaSeconds.Value;

            return targetMethod.Invoke(Inner, args);
        }
    }
}
