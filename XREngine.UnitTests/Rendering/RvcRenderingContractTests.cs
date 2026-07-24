using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RvcRenderingContractTests
{
    [Test]
    public void PipelineResolver_UsesForwardPlusOracleAsExplicitBypass()
    {
        RvcRenderingSettings settings = RvcRenderingSettings.Defaults with
        {
            PipelineMode = ERvcPipelineMode.ForwardPlusOracle,
        };

        RvcPipelineResolution resolution = RvcPipelineResolver.Resolve(
            settings,
            RvcCapabilityMatrix.ForwardPlusOnly(vulkanBackend: true));

        resolution.RequestedMode.ShouldBe(ERvcPipelineMode.ForwardPlusOracle);
        resolution.EffectiveMode.ShouldBe(ERvcPipelineMode.ForwardPlusOracle);
        resolution.IsRvcActive.ShouldBeFalse();
        resolution.FallbackReason.ShouldBe(ERvcFallbackReason.None);
        resolution.Diagnostic.ShouldContain("oracle");
    }

    [Test]
    public void PipelineResolver_ReportsFirstMissingRequiredCacheResource()
    {
        RvcRenderingSettings settings = RvcRenderingSettings.Defaults with
        {
            PipelineMode = ERvcPipelineMode.VisibilityOnlyDebug,
        };

        RvcPipelineResolution resolution = RvcPipelineResolver.Resolve(
            settings,
            RvcCapabilityMatrix.ForwardPlusOnly(vulkanBackend: true));

        resolution.IsRvcActive.ShouldBeFalse();
        resolution.EffectiveMode.ShouldBe(ERvcPipelineMode.ForwardPlusOracle);
        resolution.FallbackReason.ShouldBe(ERvcFallbackReason.MissingVisibilityTargets);
        resolution.UsesForwardPlusFallback.ShouldBeTrue();
    }

    [Test]
    public void PipelineResolver_PrefersDescriptorHeapThenIndexing()
    {
        RvcRenderingSettings settings = RvcRenderingSettings.Defaults with
        {
            PipelineMode = ERvcPipelineMode.MaterialCache,
        };

        RvcCapabilityMatrix heapCapabilities = CreateFullCacheCapabilities(
            descriptorHeapSupported: true,
            descriptorIndexingSupported: true);
        RvcCapabilityMatrix indexingCapabilities = CreateFullCacheCapabilities(
            descriptorHeapSupported: false,
            descriptorIndexingSupported: true);

        RvcPipelineResolver.Resolve(settings, heapCapabilities).DescriptorBackend
            .ShouldBe(ERvcDescriptorBackend.DescriptorHeap);
        RvcPipelineResolver.Resolve(settings, indexingCapabilities).DescriptorBackend
            .ShouldBe(ERvcDescriptorBackend.DescriptorIndexing);
    }

    [Test]
    public void PipelineResolver_RejectsOpenGlForFullProductionMode()
    {
        RvcRenderingSettings settings = RvcRenderingSettings.Defaults with
        {
            PipelineMode = ERvcPipelineMode.Full,
        };

        RvcCapabilityMatrix capabilities = CreateFullCacheCapabilities(
            vulkanBackend: false,
            openGlBackend: true,
            descriptorHeapSupported: false,
            descriptorIndexingSupported: true);

        RvcPipelineResolution resolution = RvcPipelineResolver.Resolve(settings, capabilities);

        resolution.IsRvcActive.ShouldBeFalse();
        resolution.FallbackReason.ShouldBe(ERvcFallbackReason.UnsupportedOpenGlProductionPath);
        resolution.Diagnostic.ShouldContain("Vulkan-only");
    }

    [Test]
    public void VisibilityPayload_RoundTripsAndFlagsOverflow()
    {
        bool packed = RvcVisibilityPayload.TryPack(
            instanceId: 17u,
            drawOrMeshletId: 33u,
            primitiveId: 65u,
            flags: 2u,
            out RvcVisibilityPayload payload);

        packed.ShouldBeTrue();
        payload.InstanceId.ShouldBe(17u);
        payload.DrawOrMeshletId.ShouldBe(33u);
        payload.PrimitiveId.ShouldBe(65u);
        payload.Flags.ShouldBe(2u);
        payload.HasOverflow.ShouldBeFalse();

        RvcVisibilityPayload.TryPack(
            instanceId: 1u << 20,
            drawOrMeshletId: 0u,
            primitiveId: 0u,
            flags: 0u,
            out RvcVisibilityPayload overflow).ShouldBeFalse();
        overflow.HasOverflow.ShouldBeTrue();
    }

    [Test]
    public void ShadeletReuseValidator_DefaultsToRejectingStereoReuseUntilValidated()
    {
        RvcShadeletReuseCandidate source = CreateReuseCandidate();
        RvcShadeletReuseCandidate target = source;

        bool canReuse = RvcShadeletReuseValidator.CanReuse(
            source,
            target,
            stereoReuse: false,
            maxNormalAngleDegrees: 5.0f,
            maxDepthDeltaMeters: 0.05f,
            maxRoughnessBucketDelta: 1,
            out ERvcFallbackReason rejectionReason);

        canReuse.ShouldBeFalse();
        rejectionReason.ShouldBe(ERvcFallbackReason.StereoReuseDisabledUntilValidated);
    }

    [Test]
    public void ShadeletReuseValidator_AcceptsOnlyStableIdentityAndSurfaceState()
    {
        RvcShadeletReuseCandidate source = CreateReuseCandidate();
        RvcShadeletReuseCandidate target = source with
        {
            Normal = Vector3.Normalize(new Vector3(0.01f, 0.0f, 1.0f)),
            DepthMeters = source.DepthMeters + 0.01f,
        };

        RvcShadeletReuseValidator.CanReuse(
            source,
            target,
            stereoReuse: true,
            maxNormalAngleDegrees: 5.0f,
            maxDepthDeltaMeters: 0.05f,
            maxRoughnessBucketDelta: 1,
            out ERvcFallbackReason acceptedReason).ShouldBeTrue();
        acceptedReason.ShouldBe(ERvcFallbackReason.None);

        RvcShadeletReuseCandidate staleMaterial = target with { MaterialResourceGeneration = 99u };
        RvcShadeletReuseValidator.CanReuse(
            source,
            staleMaterial,
            stereoReuse: true,
            maxNormalAngleDegrees: 5.0f,
            maxDepthDeltaMeters: 0.05f,
            maxRoughnessBucketDelta: 1,
            out ERvcFallbackReason staleReason).ShouldBeFalse();
        staleReason.ShouldBe(ERvcFallbackReason.UnsupportedMaterialClass);
    }

    [Test]
    public void FrameGraphContract_NamesPerViewResourcesByViewIdentity()
    {
        string name = RvcFrameGraphContract.MakePerViewName(
            RvcFrameGraphContract.PerViewVisibility,
            2,
            EVrOutputViewKind.LeftInset);

        name.ShouldBe("RVC/View2.LeftInset/VisibilityID");

        RvcFrameGraphResourceDescriptor[] resources = RvcFrameGraphContract.DefaultResources.ToArray();
        resources.ShouldContain(resource => resource.Name == RvcFrameGraphContract.PerViewDepth && resource.IsPerView);
        resources.ShouldContain(resource => resource.Name == RvcFrameGraphContract.SharedMaterialShadelets && !resource.IsPerView);
        resources.ShouldContain(resource => resource.Name == RvcFrameGraphContract.MirrorDebug);
    }

    [Test]
    public void QualityTolerances_AreStrictestInFovea()
    {
        RvcQualityToleranceSet tolerances = RvcQualityToleranceSet.Default;

        tolerances.Foveal.MaxPerPixelError.ShouldBeLessThan(tolerances.GuardBand.MaxPerPixelError);
        tolerances.GuardBand.MaxPerPixelError.ShouldBeLessThan(tolerances.MidField.MaxPerPixelError);
        tolerances.MidField.MaxPerPixelError.ShouldBeLessThan(tolerances.Periphery.MaxPerPixelError);
        tolerances.Foveal.MinSsim.ShouldBeGreaterThan(tolerances.Periphery.MinSsim);
    }

    [Test]
    public void HeadSpaceClusterKey_IsWorldAlignedCameraRelative()
    {
        Vector3 origin = new(10.0f, 2.0f, -3.0f);
        Vector3 world = new(10.49f, 2.51f, -2.01f);

        RvcHeadSpaceClusterKey key = RvcHeadSpaceClusterKey.FromWorldPosition(world, origin, cellSizeMeters: 0.5f);

        key.ShouldBe(new RvcHeadSpaceClusterKey(0, 1, 1));
    }

    [Test]
    public void LightReservoir_CombineConservesWeightAndCandidateCount()
    {
        RvcLightReservoir a = RvcLightReservoir.Empty.Add(7u, 0.25f, random01: 0.0f);
        RvcLightReservoir b = RvcLightReservoir.Empty.Add(9u, 0.75f, random01: 0.0f);

        RvcLightReservoir combined = RvcLightReservoir.Combine(a, b, random01: 0.0f);

        combined.SelectedLightId.ShouldBe(9u);
        combined.WeightSum.ShouldBe(1.0f, tolerance: 0.0001f);
        combined.CandidateCount.ShouldBe(2u);
    }

    [Test]
    public void TemporalHashGridKey_IsStableForSameSurfaceClass()
    {
        RvcTemporalHashGridKey a = RvcTemporalHashGridKey.FromSurface(
            new Vector3(1.2f, 2.4f, 3.6f),
            Vector3.UnitZ,
            cellSizeMeters: 0.25f,
            roughnessBucket: 4);
        RvcTemporalHashGridKey b = RvcTemporalHashGridKey.FromSurface(
            new Vector3(1.2f, 2.4f, 3.6f),
            Vector3.UnitZ,
            cellSizeMeters: 0.25f,
            roughnessBucket: 4);

        a.ShouldBe(b);
    }

    [Test]
    public void ValidationContracts_DefaultToDeterministicSideBySideCapture()
    {
        RvcValidationCaptureContract capture = RvcValidationCaptureContract.CreateDefault(ERvcValidationScene.QuadView);
        RvcAbHarnessContract harness = RvcAbHarnessContract.Default(ERvcValidationScene.QuadView);

        capture.CameraName.ShouldBe("RVC.FixedValidationCamera");
        capture.FixedAnimationTime.ShouldBeTrue();
        capture.IdenticalSceneState.ShouldBeTrue();
        harness.ComparePerRegion.ShouldBeTrue();
        harness.RequireSideBySideImages.ShouldBeTrue();
        harness.RequireHumanReviewBeforeDefaultStereoReuse.ShouldBeTrue();
    }

    [Test]
    public void RuntimePlan_UsesDelayedCountersAndConservativeCacheDefaults()
    {
        RvcRenderingSettings settings = RvcRenderingSettings.Defaults with
        {
            PipelineMode = ERvcPipelineMode.Full,
            StereoReuseEnabled = false,
        };
        RvcCapabilityMatrix capabilities = CreateFullCacheCapabilities();
        RvcPipelineResolution resolution = RvcPipelineResolver.Resolve(settings, capabilities);

        RvcPipelinePlan plan = RvcPipelinePlan.Build(settings, RvcQualitySettings.Defaults, resolution, capabilities);

        plan.CounterReadback.Mode.ShouldBe(ERvcCounterReadbackMode.DelayedGpuReadback);
        plan.CounterReadback.DoubleBuffered.ShouldBeTrue();
        plan.CounterReadback.SynchronousReadbackForbidden.ShouldBeTrue();
        plan.Visibility.Targets.PayloadFormat.ShouldBe(ERvcVisibilityPayloadFormat.Rg32UintIdentity64);
        plan.Visibility.RenderWideBeforeInset.ShouldBeTrue();
        plan.Visibility.SeedInsetHzbFromWideDepth.ShouldBeTrue();
        plan.ShadeletDeduplication.TileLocalSharedMemoryDedup.ShouldBeTrue();
        plan.ShadeletDeduplication.GlobalMergeTileSurvivors.ShouldBeTrue();
        plan.FoveatedShadingRate.RequiresComputeFor8x8.ShouldBeTrue();
        plan.WideInsetComposition.WideUnderInsetDensity.ShouldBe(ERvcShadeletDensity.Rate8x8);
        plan.WideInsetComposition.DisablePerViewSpecularUnderInset.ShouldBeTrue();
        plan.MaterialBinding.DescriptorHeapPreferred.ShouldBeTrue();
        plan.MaterialBinding.DescriptorIndexingRowsSemanticallyIdentical.ShouldBeTrue();
        plan.MaterialBinding.ShadeletKeysExcludeBackendDescriptorHandles.ShouldBeTrue();
        plan.Reuse.Policy.EnabledDomains.HasFlag(ERvcReuseDomain.Stereo).ShouldBeFalse();
        plan.Resolve.FovealAntiAliasingPath.ShouldBe(ERvcFovealAntiAliasingPath.VisibilityEdgeAA);
        plan.OpenXrVisibilityMask.CanStencil.ShouldBeTrue();
        plan.VisibilitySourcePaths.HasRequiredPaths.ShouldBeTrue();
        plan.GpuPassExecution.PlannedStages.HasFlag(ERvcGpuPassStage.VisibilityTargets).ShouldBeTrue();
        plan.GpuPassExecution.PlannedStages.HasFlag(ERvcGpuPassStage.FoveatedResolve).ShouldBeTrue();
        plan.VulkanProduction.RequiredFeatures.HasFlag(ERvcVulkanProductionFeature.Synchronization2).ShouldBeTrue();
        plan.ExperimentalExtensions.EyeTrackedFoveation.ShouldBe(ERvcExperimentalExtensionPolicy.CapabilityAdvertised);
    }

    [Test]
    public void DelayedCounterReadback_ForbidsSynchronousRenderLoopReadback()
    {
        RvcCounterReadbackContract contract = RvcCounterReadbackContract.Default;

        RvcDelayedCounterReadbackDecision forbidden = contract.Evaluate(
            currentFrameId: 12UL,
            producedFrameId: 12UL,
            synchronousReadbackRequested: true);
        RvcDelayedCounterReadbackDecision pending = contract.Evaluate(
            currentFrameId: 13UL,
            producedFrameId: 12UL,
            synchronousReadbackRequested: false);
        RvcDelayedCounterReadbackDecision ready = contract.Evaluate(
            currentFrameId: 14UL,
            producedFrameId: 12UL,
            synchronousReadbackRequested: false);

        forbidden.Decision.ShouldBe(ERvcCounterReadbackDecision.SynchronousForbidden);
        forbidden.FallbackReason.ShouldBe(ERvcFallbackReason.SynchronousCounterReadbackForbidden);
        forbidden.AllowReadback.ShouldBeFalse();
        pending.Decision.ShouldBe(ERvcCounterReadbackDecision.Pending);
        pending.AllowReadback.ShouldBeFalse();
        ready.Decision.ShouldBe(ERvcCounterReadbackDecision.Ready);
        ready.AllowReadback.ShouldBeTrue();
    }

    [Test]
    public void FrameProfileSnapshot_RecordsQuadViewIdentityAndPixels()
    {
        RvcFrameViewDiagnostics[] views =
        [
            CreateFrameViewDiagnostics(0u, EVrOutputViewKind.LeftWide, 100u, 50u),
            CreateFrameViewDiagnostics(1u, EVrOutputViewKind.RightWide, 100u, 50u),
            CreateFrameViewDiagnostics(2u, EVrOutputViewKind.LeftInset, 40u, 30u),
            CreateFrameViewDiagnostics(3u, EVrOutputViewKind.RightInset, 40u, 30u),
        ];
        RvcFrameViewProjectionDiagnostics[] projections =
        [
            CreateProjectionDiagnostics(0u, 100, 50),
            CreateProjectionDiagnostics(1u, 100, 50),
            CreateProjectionDiagnostics(2u, 40, 30),
            CreateProjectionDiagnostics(3u, 40, 30),
        ];

        RvcFrameProfileSnapshot profile = RvcFrameProfileSnapshot.Create(
            frameId: 27UL,
            predictedDisplayTime: 1234L,
            views,
            projections,
            ERvcFallbackReason.None,
            "quad view profile");

        profile.FrameId.ShouldBe(27UL);
        profile.ViewCount.ShouldBe(4);
        profile.ViewSet.IsQuadViewSet.ShouldBeTrue();
        profile.ViewSet.TotalPixelCount.ShouldBe(12_400UL);
        profile.GetView(2).ViewKind.ShouldBe(EVrOutputViewKind.LeftInset);
        profile.GetProjection(3).ViewportWidth.ShouldBe(40);
        profile.GetProjection(3).PreviousViewProjectionMatrix.ShouldBe(Matrix4x4.Identity);
    }

    [Test]
    public void RuntimeCodePlans_ReportMaskSourceAndVulkanGaps()
    {
        RvcRenderingSettings settings = RvcRenderingSettings.Defaults with
        {
            PipelineMode = ERvcPipelineMode.Full,
            QuadViewEnabled = true,
        };
        RvcPipelineResolution fallback = RvcPipelineResolver.Resolve(
            settings,
            RvcCapabilityMatrix.ForwardPlusOnly(vulkanBackend: true));

        RvcPipelinePlan plan = RvcPipelinePlan.Build(settings, RvcQualitySettings.Defaults, fallback, RvcCapabilityMatrix.ForwardPlusOnly(vulkanBackend: true));

        plan.OpenXrVisibilityMask.FallbackReason.ShouldBe(ERvcFallbackReason.MissingVisibilityMask);
        plan.VisibilitySourcePaths.EnabledPaths.HasFlag(ERvcVisibilitySourcePath.ForwardPlusOracle).ShouldBeTrue();
        plan.GpuPassExecution.PlannedStages.HasFlag(ERvcGpuPassStage.VisibilityTargets).ShouldBeTrue();
        plan.GpuPassExecution.ForwardPlusFallbackStages.ShouldNotBe(ERvcGpuPassStage.None);
        plan.VulkanProduction.MissingFeatures.HasFlag(ERvcVulkanProductionFeature.DescriptorIndexing).ShouldBeFalse();
    }

    [Test]
    public void ViewSetDiagnostics_AggregatesRuntimeReportedViewPixels()
    {
        RenderFrameViewDescriptor[] views =
        [
            CreateView(0u, EVrOutputViewKind.LeftWide, 100u, 50u),
            CreateView(1u, EVrOutputViewKind.RightWide, 100u, 50u),
            CreateView(2u, EVrOutputViewKind.LeftInset, 40u, 30u, parentViewId: 0u),
            CreateView(3u, EVrOutputViewKind.RightInset, 40u, 30u, parentViewId: 1u),
        ];
        RenderFrameViewSet viewSet = RenderFrameViewSet.Create(
            EVrViewRenderMode.SequentialViews,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 2,
            views,
            "TestQuad");

        RvcViewSetDiagnostics diagnostics = RvcViewSetDiagnostics.FromViewSet(viewSet, ERvcFallbackReason.MissingQuadViewRuntime);

        diagnostics.ViewCount.ShouldBe(4);
        diagnostics.IsQuadViewSet.ShouldBeTrue();
        diagnostics.TotalPixelCount.ShouldBe(12_400UL);
        diagnostics.FallbackReason.ShouldBe(ERvcFallbackReason.MissingQuadViewRuntime);
    }

    [Test]
    public void MaterialClassifierAndShadeletBudget_RouteFallbacksConservatively()
    {
        RvcMaterialClassifier.Classify(
            transparent: true,
            refractiveOrOrderDependent: false,
            expensiveAlphaTest: false,
            generatedMaterialTableOpaque: false,
            unlit: false,
            pbr: true).ShouldBe(ERvcMaterialClass.TransparentForwardPlusFallback);

        RvcMaterialClassifier.Classify(
            transparent: false,
            refractiveOrOrderDependent: false,
            expensiveAlphaTest: false,
            generatedMaterialTableOpaque: true,
            unlit: false,
            pbr: true).ShouldBe(ERvcMaterialClass.GeneratedMaterialTableOpaque);

        RvcShadeletMapBudget budget = RvcShadeletMapBudget.Default;
        budget.Check(1u, 1u, 1u, 1u).ShouldBe(ERvcFallbackReason.None);
        budget.Check(budget.MaxShadeletsPerView + 1u, 1u, 1u, 1u).ShouldBe(ERvcFallbackReason.ShadeletMapOverflow);
        budget.Check(1u, 1u, budget.MaxMaterialBins + 1u, 1u).ShouldBe(ERvcFallbackReason.DeduplicationOverflow);
    }

    [Test]
    public void SourceContracts_RvcPipelineSettingsAndFallbackStayWired()
    {
        string contracts = ReadWorkspaceFile("XREngine.Runtime.Core/Settings/RvcRenderingContracts.cs");
        string runtimeContracts = ReadWorkspaceFile("XREngine.Runtime.Core/Settings/RvcRuntimeContracts.cs");
        string pipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RvcRenderPipeline.cs");
        string engineFactory = ReadWorkspaceFile("XREngine/Engine/Subclasses/Rendering/Engine.Rendering.cs");
        string settings = ReadWorkspaceFile("XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");
        string openXrViewConfiguration = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.ViewConfiguration.cs");
        string openXrOpenGl = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs");
        string openXrVulkan = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string openXrState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string openXrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string openXrFrameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string openXrExtensions = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/Extensions.cs");
        string hostStatistics = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderStatisticsServices.cs");
        string hostPresentation = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderPresentationServices.cs");
        string rvcPass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RvcPass.cs");
        string rendererHost = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRendererHost.cs");
        string abstractRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs");
        string vulkanMeshlets = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Meshlets/VulkanRenderer.Meshlets.cs");

        contracts.ShouldContain("public enum ERvcPipelineMode");
        contracts.ShouldContain("public static class RvcFrameGraphContract");
        contracts.ShouldContain("RvcShadeletReuseValidator");
        runtimeContracts.ShouldContain("public readonly record struct RvcPipelinePlan");
        runtimeContracts.ShouldContain("RvcCounterReadbackContract.Default");
        runtimeContracts.ShouldContain("RvcVisibilityPassPlan.Default");
        runtimeContracts.ShouldContain("RvcOpenXrVisibilityMaskPlan");
        runtimeContracts.ShouldContain("RvcVisibilitySourcePathPlan");
        runtimeContracts.ShouldContain("RvcGpuPassExecutionPlan");
        runtimeContracts.ShouldContain("RvcDelayedCounterReadbackDecision");
        runtimeContracts.ShouldContain("RvcWideInsetCompositionPolicy.Default");
        runtimeContracts.ShouldContain("RvcMaterialBindingContract.FromResolution");
        runtimeContracts.ShouldContain("RvcSharedLightingPlan.CreateDefault");
        pipeline.ShouldContain("public sealed class RvcRenderPipeline : DefaultRenderPipeline");
        pipeline.ShouldContain("DeclareRvcResources");
        pipeline.ShouldContain("AppendRvcPassCommands");
        pipeline.ShouldContain("renderer?.SupportsRvcVisibilityTargets");
        pipeline.ShouldContain("Debug.RenderingWarningEvery");
        pipeline.ShouldContain("RvcFrameGraphContract");
        pipeline.ShouldContain("RvcFrameGraphContract.PerViewDepthArray");
        pipeline.ShouldContain("RvcFrameGraphContract.SharedMaterialResourceRows");
        pipeline.ShouldContain("RvcPipelinePlan.Build");
        rvcPass.ShouldContain("public class VPRC_RvcPass");
        rvcPass.ShouldContain("ERvcGpuPassStage.OpenXrVisibilityMaskStencil");
        rvcPass.ShouldContain("RvcFrameGraphContract.SharedTemporalCache");
        rvcPass.ShouldContain("RenderGraphResourceNames.MakeTexture");
        engineFactory.ShouldContain("Settings.RvcPipelineMode != ERvcPipelineMode.Off");
        engineFactory.ShouldContain("NewRvcRenderPipeline");
        settings.ShouldContain("public ERvcPipelineMode RvcPipelineMode");
        settings.ShouldContain("public bool RvcStereoReuseEnabled");
        settings.ShouldContain("IsRvcPipelineSetting");
        openXrViewConfiguration.ShouldContain("InitializeOpenXrViewsForActiveConfiguration");
        openXrViewConfiguration.ShouldContain("PrimaryQuadVarjoViewConfigurationType");
        openXrViewConfiguration.ShouldContain("CacheOpenXrViewConfigurationSnapshots");
        openXrViewConfiguration.ShouldContain("IsLeftEyeLikeOpenXrView");
        openXrViewConfiguration.ShouldContain("ResolveOpenXrRvcViewKind");
        openXrViewConfiguration.ShouldContain("InitializeOpenXrRvcVisibilityMaskStates");
        openXrViewConfiguration.ShouldContain("xrGetVisibilityMaskKHR");
        openXrViewConfiguration.ShouldContain("TryFetchOpenXrRvcVisibilityMaskMesh");
        openXrOpenGl.ShouldContain("InitializeOpenXrViewsForActiveConfiguration(\"OpenXR OpenGL\")");
        openXrVulkan.ShouldContain("InitializeOpenXrViewsForActiveConfiguration(\"OpenXR Vulkan\")");
        openXrVulkan.ShouldContain("RenderFrameViewSet.MaxViewCount");
        openXrState.ShouldContain("private readonly Swapchain[] _swapchains = new Swapchain[RenderFrameViewSet.MaxViewCount]");
        openXrState.ShouldContain("_nonFoveatedStereoViewConfigViews");
        openXrState.ShouldContain("_foveatedQuadViewConfigViews");
        openXrCalls.ShouldContain("ViewConfigurationType = _activeViewConfigurationType");
        openXrCalls.ShouldContain("PrimaryViewConfigurationType = _activeViewConfigurationType");
        openXrCalls.ShouldContain("InvalidateOpenXrRvcVisibilityMasks");
        openXrFrameLifecycle.ShouldContain("PublishOpenXrRvcFrameProfile");
        openXrFrameLifecycle.ShouldContain("RecordOpenXrRvcFrameViewDiagnostics");
        openXrFrameLifecycle.ShouldContain("RecordOpenXrRvcFrameViewProjectionDiagnostics");
        openXrFrameLifecycle.ShouldContain("ResolveRvcViewGpuMilliseconds");
        openXrExtensions.ShouldContain("XR_KHR_visibility_mask");
        hostStatistics.ShouldContain("RecordRenderRvcFrameCounters");
        hostStatistics.ShouldContain("RecordRenderRvcFrameProfile");
        hostPresentation.ShouldContain("ERvcPipelineMode RvcPipelineMode");
        hostPresentation.ShouldContain("RvcOpenXrVisibilityMaskEnabled");
        rendererHost.ShouldContain("SupportsRvcMaterialResourceTable");
        rendererHost.ShouldContain("SupportsRvcVisibilityTargets");
        abstractRenderer.ShouldContain("RvcDescriptorBackend");
        vulkanMeshlets.ShouldContain("RvcVulkanProductionFeatures");
    }

    private static RvcCapabilityMatrix CreateFullCacheCapabilities(
        bool vulkanBackend = true,
        bool openGlBackend = false,
        bool descriptorHeapSupported = true,
        bool descriptorIndexingSupported = true)
        => new(
            ForwardPlusOracleAvailable: true,
            FrameGraphAvailable: true,
            VisibilityTargetsAvailable: true,
            VulkanBackend: vulkanBackend,
            OpenGlBackend: openGlBackend,
            DescriptorHeapSupported: descriptorHeapSupported,
            DescriptorIndexingSupported: descriptorIndexingSupported,
            FragmentShadingRateSupported: true,
            FragmentDensityMapSupported: false,
            OpenXrQuadViewsSupported: true,
            OpenXrRuntimeFoveationSupported: false,
            OpenXrDepthLayersSupported: true,
            OpenXrVisibilityMaskSupported: true,
            MultiviewSupported: true);

    private static RvcShadeletReuseCandidate CreateReuseCandidate()
    {
        RvcVisibilityPayload.TryPack(3u, 5u, 7u, 0u, out RvcVisibilityPayload visibility)
            .ShouldBeTrue();

        RvcSurfaceKey surface = new(
            QuantizedU: 1024,
            QuantizedV: 2048,
            RoughnessBucket: 6,
            LodBucket: 1,
            ERvcFoveationRegion.Foveal);
        RvcShadeletKey key = RvcShadeletKey.Create(
            visibility,
            ERvcMaterialClass.OpaquePbr,
            surface,
            materialRowId: 11u,
            deformationVersion: 13u);

        return new(
            key,
            MaterialResourceGeneration: 17u,
            Normal: Vector3.UnitZ,
            DepthMeters: 2.0f,
            RoughnessBucket: 6,
            DeformationVersion: 13u,
            LodBucket: 1,
            Disoccluded: false,
            ViewDependentMaterial: false);
    }

    private static RenderFrameViewDescriptor CreateView(
        uint viewId,
        EVrOutputViewKind kind,
        uint width,
        uint height,
        uint parentViewId = RenderFrameViewDescriptor.InvalidViewId)
        => new(
            viewId,
            kind,
            parentViewId,
            VisibilityGroupIndex: kind is EVrOutputViewKind.LeftWide or EVrOutputViewKind.LeftInset ? 0 : 1,
            OpenXrViewIndex: (int)viewId,
            OutputLayer: viewId,
            RenderFrameViewRect.FromSize(width, height),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            ViewFoveationContext.Off(),
            DebugName: kind.ToString());

    private static RvcFrameViewDiagnostics CreateFrameViewDiagnostics(
        uint viewId,
        EVrOutputViewKind kind,
        uint width,
        uint height)
        => new(
            viewId,
            kind,
            width,
            height,
            HorizontalFovDegrees: 90.0f,
            VerticalFovDegrees: 80.0f,
            SwapchainIdentity: viewId + 1UL,
            PixelCount: (ulong)width * height,
            GpuMilliseconds: 0.0,
            EVrViewRenderMode.SequentialViews,
            EVrFoveationMode.Off,
            ERvcFallbackReason.None);

    private static RvcFrameViewProjectionDiagnostics CreateProjectionDiagnostics(
        uint viewId,
        int width,
        int height)
        => new(
            viewId,
            RuntimeViewIndex: (int)viewId,
            ViewportX: 0,
            ViewportY: 0,
            ViewportWidth: width,
            ViewportHeight: height,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity);

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        while (dir is not null)
        {
            string fullPath = Path.Combine(dir.FullName, platformPath);
            if (File.Exists(fullPath))
                return File.ReadAllText(fullPath).Replace("\r\n", "\n");

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
