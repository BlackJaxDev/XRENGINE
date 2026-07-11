using NUnit.Framework;
using Shouldly;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VrViewRenderModeContractTests
{
    [TestCase(true, false, false, true)]
    [TestCase(true, false, true, false)]
    [TestCase(true, true, false, false)]
    [TestCase(false, false, false, false)]
    public void StrictSinglePassStereo_FinalGuardBlocksOnlyUnhandledSequentialFallback(
        bool strictRequested,
        bool allEyesRendered,
        bool batchHandled,
        bool expectedBlocked)
    {
        OpenXRAPI.MustBlockStrictSinglePassStereoSequentialFallback(
            strictRequested,
            allEyesRendered,
            batchHandled).ShouldBe(expectedBlocked);
    }

    [Test]
    public void ViewRenderModeResolver_ExposesBackendSupportMatrix()
    {
        VrViewRenderModeResolution vulkanSequential =
            VrViewRenderModeResolver.Resolve(ERenderLibrary.Vulkan, EVrViewRenderMode.SequentialViews);
        VrViewRenderModeResolution vulkanSinglePass =
            VrViewRenderModeResolver.Resolve(ERenderLibrary.Vulkan, EVrViewRenderMode.SinglePassStereo);
        VrViewRenderModeResolution vulkanParallel =
            VrViewRenderModeResolver.Resolve(ERenderLibrary.Vulkan, EVrViewRenderMode.ParallelCommandBufferRecording);

        vulkanSequential.IsSupported.ShouldBeTrue();
        vulkanSequential.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.SequentialViews);
        vulkanSequential.TemporalHistoryPolicy.ShouldBe(EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain);
        vulkanSinglePass.IsSupported.ShouldBeFalse();
        vulkanSinglePass.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.Unsupported);
        vulkanSinglePass.TemporalHistoryPolicy.ShouldBe(EVrTemporalHistoryPolicy.Disabled);
        vulkanSinglePass.Diagnostic!.ShouldContain("Sequential or per-eye compatibility fallback is forbidden");
        vulkanParallel.IsSupported.ShouldBeTrue();
        vulkanParallel.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.ParallelCommandBufferRecording);

        VrViewRenderModeResolver.Resolve(ERenderLibrary.OpenGL, EVrViewRenderMode.SequentialViews).IsSupported.ShouldBeTrue();
        VrViewRenderModeResolver.Resolve(ERenderLibrary.OpenGL, EVrViewRenderMode.SinglePassStereo).IsSupported.ShouldBeFalse();

        VrViewRenderModeResolution openGlParallel =
            VrViewRenderModeResolver.Resolve(ERenderLibrary.OpenGL, EVrViewRenderMode.ParallelCommandBufferRecording);
        openGlParallel.IsSupported.ShouldBeFalse();
        openGlParallel.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.Unsupported);
        openGlParallel.TemporalHistoryPolicy.ShouldBe(EVrTemporalHistoryPolicy.Disabled);
        openGlParallel.Diagnostic!.ShouldContain("Vulkan-only");
    }

    [Test]
    public void ViewRenderModeResolver_RequiresTrueStereoAndNeverSelectsCompatibilityFallback()
    {
        VrViewRenderModeResolution unavailable = VrViewRenderModeResolver.Resolve(
            ERenderLibrary.Vulkan,
            EVrViewRenderMode.SinglePassStereo,
            trueSinglePassStereoAvailable: false,
            trueSinglePassStereoUnavailableReason: "multiview feature was not enabled");
        unavailable.IsSupported.ShouldBeFalse();
        unavailable.EffectiveMode.ShouldBe(EVrViewRenderMode.SinglePassStereo);
        unavailable.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.Unsupported);
        unavailable.TemporalHistoryPolicy.ShouldBe(EVrTemporalHistoryPolicy.Disabled);
        unavailable.Diagnostic!.ShouldContain("multiview feature was not enabled");
        unavailable.Diagnostic!.ShouldContain("fallback is forbidden");

        VrViewRenderModeResolution trueStereo = VrViewRenderModeResolver.Resolve(
            ERenderLibrary.Vulkan,
            EVrViewRenderMode.SinglePassStereo,
            trueSinglePassStereoAvailable: true,
            rendersExternalSwapchainTargets: false);
        trueStereo.EffectiveMode.ShouldBe(EVrViewRenderMode.SinglePassStereo);
        trueStereo.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.TrueSinglePassStereo);
        trueStereo.TemporalHistoryPolicy.ShouldBe(EVrTemporalHistoryPolicy.StereoArrayLayer);
    }

    [Test]
    public void ViewRenderModeResolver_RejectsParallelWhenStartupGateDisablesIt()
    {
        VrViewRenderModeResolution resolution = VrViewRenderModeResolver.Resolve(
            ERenderLibrary.Vulkan,
            EVrViewRenderMode.ParallelCommandBufferRecording,
            enableOpenXrVulkanParallelRendering: false);

        resolution.IsSupported.ShouldBeFalse();
        resolution.EffectiveImplementationPath.ShouldBe(EVrViewRenderImplementationPath.Unsupported);
        resolution.Diagnostic!.ShouldContain("disabled by startup settings");
    }

    [Test]
    public void UnitTestingWorld_LegacySinglePassStereoMigratesToViewRenderMode()
    {
        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
            """
            {
              "SinglePassStereoVR": true
            }
            """);

        settings.VR.ViewRenderMode.ShouldBe(EVrViewRenderMode.SinglePassStereo);
        settings.SinglePassStereoVR.ShouldBeTrue();
    }

    [Test]
    public void UnitTestingWorld_ExplicitViewRenderModeWinsOverLegacySinglePassStereo()
    {
        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
            """
            {
              "VR": {
                "Mode": "MonadoOpenXR",
                "ViewRenderMode": "ParallelCommandBufferRecording"
              },
              "SinglePassStereoVR": false
            }
            """);

        settings.VR.ViewRenderMode.ShouldBe(EVrViewRenderMode.ParallelCommandBufferRecording);
        settings.SinglePassStereoVR.ShouldBeFalse();
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_ViewRenderModeEnvOverrideIsApplied()
    {
        string? previous = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrViewRenderMode);
        try
        {
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.UnitTestVrViewRenderMode,
                nameof(EVrViewRenderMode.SinglePassStereo));

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "ViewRenderMode": "SequentialViews"
                  }
                }
                """);

            UnitTestingWorldSettingsStore.ApplyVrLaunchOverrides(settings);

            settings.VR.ViewRenderMode.ShouldBe(EVrViewRenderMode.SinglePassStereo);
            settings.IsJsonPropertyPathSpecified(
                nameof(UnitTestingWorldSettings.VR),
                nameof(UnitTestingVrSettings.ViewRenderMode)).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrViewRenderMode, previous);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_FoveationEnvOverridesAreApplied()
    {
        string? previousMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrFoveationMode);
        string? previousQuality = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrFoveationQualityPreset);
        string? previousRequire = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrFoveationRequireRequested);
        try
        {
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.UnitTestVrFoveationMode,
                nameof(EVrFoveationMode.Fixed));
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.UnitTestVrFoveationQualityPreset,
                nameof(EVrFoveationQualityPreset.Aggressive));
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.UnitTestVrFoveationRequireRequested,
                "1");

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "Foveation": {
                      "Mode": "Off",
                      "QualityPreset": "Balanced",
                      "RequireRequested": false
                    }
                  }
                }
                """);

            UnitTestingWorldSettingsStore.ApplyVrLaunchOverrides(settings);

            settings.VR.Foveation.Mode.ShouldBe(EVrFoveationMode.Fixed);
            settings.VR.Foveation.QualityPreset.ShouldBe(EVrFoveationQualityPreset.Aggressive);
            settings.VR.Foveation.RequireRequested.ShouldBeTrue();
            settings.IsJsonPropertyPathSpecified(
                nameof(UnitTestingWorldSettings.VR),
                nameof(UnitTestingVrSettings.Foveation),
                nameof(UnitTestingVrFoveationSettings.Mode)).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrFoveationMode, previousMode);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrFoveationQualityPreset, previousQuality);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrFoveationRequireRequested, previousRequire);
        }
    }

    [Test]
    public void OpenXrEyeResolutionResolver_UsesPresetsScaleAndClampsRuntimeMaxMismatch()
    {
        OpenXRAPI.OpenXrEyeSwapchainExtent valveIndex =
            OpenXRAPI.ResolveOpenXrEyeSwapchainExtentForSettings(
                EOpenXrEyeResolutionPreset.ValveIndex,
                1.25f,
                customWidth: 0u,
                customHeight: 0u,
                recommendedWidth: 896u,
                recommendedHeight: 1007u,
                maxWidth: 4000u,
                maxHeight: 4000u);

        valveIndex.Width.ShouldBe(1800u);
        valveIndex.Height.ShouldBe(2000u);
        valveIndex.Source.ShouldContain("Valve");
        valveIndex.ExceedsRuntimeMax.ShouldBeFalse();

        OpenXRAPI.OpenXrEyeSwapchainExtent questPro =
            OpenXRAPI.ResolveOpenXrEyeSwapchainExtentForSettings(
                EOpenXrEyeResolutionPreset.QuestPro,
                0.5f,
                customWidth: 0u,
                customHeight: 0u,
                recommendedWidth: 896u,
                recommendedHeight: 1007u,
                maxWidth: 4000u,
                maxHeight: 4000u);

        questPro.Width.ShouldBe(900u);
        questPro.Height.ShouldBe(960u);

        OpenXRAPI.OpenXrEyeSwapchainExtent beyond2 =
            OpenXRAPI.ResolveOpenXrEyeSwapchainExtentForSettings(
                EOpenXrEyeResolutionPreset.BigscreenBeyond2,
                2.0f,
                customWidth: 0u,
                customHeight: 0u,
                recommendedWidth: 896u,
                recommendedHeight: 1007u,
                maxWidth: 3000u,
                maxHeight: 2800u);

        beyond2.RequestedWidth.ShouldBe(5120u);
        beyond2.RequestedHeight.ShouldBe(5120u);
        beyond2.Width.ShouldBe(3000u);
        beyond2.Height.ShouldBe(2800u);
        beyond2.ExceedsRuntimeMax.ShouldBeTrue();

        OpenXRAPI.OpenXrEyeSwapchainExtent custom =
            OpenXRAPI.ResolveOpenXrEyeSwapchainExtentForSettings(
                EOpenXrEyeResolutionPreset.Custom,
                0.5f,
                customWidth: 2048u,
                customHeight: 1600u,
                recommendedWidth: 896u,
                recommendedHeight: 1007u,
                maxWidth: 0u,
                maxHeight: 0u);

        custom.Width.ShouldBe(1024u);
        custom.Height.ShouldBe(800u);
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_OpenXrEyeResolutionEnvOverridesAreApplied()
    {
        string? previousPreset = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionPreset);
        string? previousScale = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionScale);
        string? previousWidth = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionWidth);
        string? previousHeight = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionHeight);
        try
        {
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionPreset,
                nameof(EOpenXrEyeResolutionPreset.Custom));
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionScale, "1.5");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionWidth, "2048");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionHeight, "2048");

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrEyeResolution": {
                      "Preset": "RuntimeRecommended",
                      "Scale": 1.0,
                      "CustomWidth": 0,
                      "CustomHeight": 0
                    }
                  }
                }
                """);

            UnitTestingWorldSettingsStore.ApplyVrLaunchOverrides(settings);

            settings.VR.OpenXrEyeResolution.Preset.ShouldBe(EOpenXrEyeResolutionPreset.Custom);
            settings.VR.OpenXrEyeResolution.Scale.ShouldBe(1.5f);
            settings.VR.OpenXrEyeResolution.CustomWidth.ShouldBe(2048u);
            settings.VR.OpenXrEyeResolution.CustomHeight.ShouldBe(2048u);
            settings.IsJsonPropertyPathSpecified(
                nameof(UnitTestingWorldSettings.VR),
                nameof(UnitTestingVrSettings.OpenXrEyeResolution),
                nameof(UnitTestingOpenXrEyeResolutionSettings.Preset)).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionPreset, previousPreset);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionScale, previousScale);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionWidth, previousWidth);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrEyeResolutionHeight, previousHeight);
        }
    }

    [Test]
    public void FoveationResolver_ReportsVulkanCapabilityPath()
    {
        VrFoveationResolution resolution = VrFoveationResolver.Resolve(
            ERenderLibrary.Vulkan,
            EVrFoveationMode.Fixed,
            EVrFoveationQualityPreset.Balanced,
            requireRequested: true,
            new VrFoveationBackendCapabilities(
                VulkanFragmentShadingRate: true,
                VulkanFragmentDensityMap: false,
                OpenXrRuntimeFoveation: false,
                OpenXrQuadViews: false,
                OpenGlFixedFoveationExtension: false,
                OpenGlMultiResolution: false));

        resolution.IsSupported.ShouldBeTrue();
        resolution.EffectiveMode.ShouldBe(EVrFoveationMode.Fixed);
        resolution.CapabilityPath.ShouldBe(EVrFoveationCapabilityPath.VulkanFragmentShadingRate);
    }

    [Test]
    public void FoveationResolver_OpenGlUnsupportedRequestReportsDiagnostic()
    {
        VrFoveationResolution resolution = VrFoveationResolver.Resolve(
            ERenderLibrary.OpenGL,
            EVrFoveationMode.Fixed,
            EVrFoveationQualityPreset.Conservative,
            requireRequested: true,
            VrFoveationBackendCapabilities.None);

        resolution.IsSupported.ShouldBeFalse();
        resolution.EffectiveMode.ShouldBe(EVrFoveationMode.Fixed);
        resolution.Diagnostic!.ShouldContain("not supported on OpenGL");
    }

    [Test]
    public void ViewFoveationContext_VulkanAttachmentPathsArePlannerOwned()
    {
        ViewFoveationAttachmentContext shadingRate = ViewFoveationAttachmentContext.FromCapability(
            EVrFoveationCapabilityPath.VulkanFragmentShadingRate,
            backendResourceKey: 0x1234UL);
        ViewFoveationAttachmentContext densityMap = ViewFoveationAttachmentContext.FromCapability(
            EVrFoveationCapabilityPath.VulkanFragmentDensityMap,
            backendResourceKey: 0x5678UL);
        ViewFoveationAttachmentContext runtimeOnly = ViewFoveationAttachmentContext.FromCapability(
            EVrFoveationCapabilityPath.OpenXrRuntimeFoveation,
            backendResourceKey: 0x9999UL);

        shadingRate.Kind.ShouldBe(EVrFoveationAttachmentKind.VulkanFragmentShadingRate);
        shadingRate.IsActive.ShouldBeTrue();
        shadingRate.OwnedByResourcePlanner.ShouldBeTrue();
        shadingRate.ResourceName!.ShouldContain("VulkanFragmentShadingRate");

        densityMap.Kind.ShouldBe(EVrFoveationAttachmentKind.VulkanFragmentDensityMap);
        densityMap.IsActive.ShouldBeTrue();
        densityMap.OwnedByResourcePlanner.ShouldBeTrue();
        densityMap.ResourceName!.ShouldContain("VulkanFragmentDensityMap");

        runtimeOnly.IsActive.ShouldBeFalse();
        runtimeOnly.OwnedByResourcePlanner.ShouldBeFalse();
    }

    [Test]
    public void ViewRenderGroup_AllowDesktopEditingTrueKeepsDesktopVisibilityIndependent()
    {
        ViewRenderGroupContext group = ViewRenderGroupContext.CreateDesktopEditingGroup(
            EVrViewRenderMode.SequentialViews,
            CreateView(EVrOutputViewKind.DesktopEditor, 0u, Matrix4x4.Identity),
            CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f)),
            CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f)),
            new ViewVisibilityFrustumContext(Matrix4x4.Identity, CreateProjection(70.0f), true, false));

        group.VisibilityPolicy.ShouldBe(EVrVisibilityPolicy.IndependentDesktopAndVrEyes);
        group.ViewCount.ShouldBe(3);
        group.VisibilityGroupCount.ShouldBe(2);
        group.GetView(0).Kind.ShouldBe(EVrOutputViewKind.DesktopEditor);
        group.GetView(0).VisibilityGroupIndex.ShouldBe(0);
        group.GetView(1).VisibilityGroupIndex.ShouldBe(1);
        group.GetView(2).VisibilityGroupIndex.ShouldBe(1);
        group.CountViewsInVisibilityGroup(0).ShouldBe(1);
        group.CountViewsInVisibilityGroup(1).ShouldBe(2);
    }

    [Test]
    public void ViewRenderGroup_AllowDesktopEditingFalseSharesOneRuntimeVisibilityGroup()
    {
        ViewRenderContext left = CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f));
        ViewRenderContext right = CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f));
        ViewRenderContext cyclopean = CreateView(EVrOutputViewKind.CyclopeanDesktop, 2u, Matrix4x4.Identity, CreateProjection(55.0f));
        ViewVisibilityFrustumContext combined = ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(left, right, cyclopean);

        ViewRenderGroupContext group = ViewRenderGroupContext.CreateCombinedRuntimeGroup(
            EVrViewRenderMode.SequentialViews,
            left,
            right,
            cyclopean,
            combined);

        group.VisibilityPolicy.ShouldBe(EVrVisibilityPolicy.CombinedRuntimeLeftRightCyclopean);
        group.VisibilityGroupCount.ShouldBe(1);
        group.AllViewsShareVisibility.ShouldBeTrue();
        group.VisibilityFrustum.IsConservative.ShouldBeTrue();
        group.CountViewsInVisibilityGroup(0).ShouldBe(3);
        group.GetView(0).Kind.ShouldBe(EVrOutputViewKind.LeftEye);
        group.GetView(1).Kind.ShouldBe(EVrOutputViewKind.RightEye);
        group.GetView(2).Kind.ShouldBe(EVrOutputViewKind.CyclopeanDesktop);
    }

    [Test]
    public void ViewRenderGroup_TwoEyeVisibilityFrustumExcludesSkippedCyclopeanView()
    {
        VrFoveationResolution resolution = new(
            EVrFoveationMode.Fixed,
            EVrFoveationMode.Fixed,
            EVrFoveationQualityPreset.Aggressive,
            EVrFoveationCapabilityPath.VulkanFragmentShadingRate,
            true,
            null);
        ViewFoveationContext cyclopeanFoveation = ViewFoveationContext.FromResolution(
            resolution,
            Vector2.Zero,
            Vector2.Zero,
            new Vector2(0.1f, 0.9f),
            EVrFoveationGazeSource.FixedCenter,
            backendResourceKey: 707UL);

        ViewRenderContext left = CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f));
        ViewRenderContext right = CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f));
        ViewRenderContext cyclopean = CreateView(
            EVrOutputViewKind.CyclopeanDesktop,
            2u,
            Matrix4x4.Identity,
            CreateProjection(55.0f),
            cyclopeanFoveation);

        ViewVisibilityFrustumContext twoEye = ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(left, right);
        ViewVisibilityFrustumContext threeView = ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(left, right, cyclopean);

        twoEye.IsConservative.ShouldBeTrue();
        twoEye.IncludesFoveatedViews.ShouldBeFalse();
        threeView.IsConservative.ShouldBeTrue();
        threeView.IncludesFoveatedViews.ShouldBeTrue();
    }

    [Test]
    public void ViewRenderGroup_CombinedRuntimeBindsOneImmutableVisibleSetToAllViews()
    {
        ViewRenderContext left = CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f));
        ViewRenderContext right = CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f));
        ViewRenderContext cyclopean = CreateView(EVrOutputViewKind.CyclopeanDesktop, 2u, Matrix4x4.Identity, CreateProjection(55.0f));
        object runtimeVisibleSet = new();

        ViewRenderGroupContext group = ViewRenderGroupContext.CreateCombinedRuntimeGroup(
                EVrViewRenderMode.ParallelCommandBufferRecording,
                left,
                right,
                cyclopean,
                ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(left, right, cyclopean))
            .BindVisibilitySet(0, runtimeVisibleSet, generation: 42UL, debugName: "left/right/cyclopean");

        ViewVisibilitySetBinding leftBinding = group.GetVisibilitySetBindingForView(0);
        ViewVisibilitySetBinding rightBinding = group.GetVisibilitySetBindingForView(1);
        ViewVisibilitySetBinding cyclopeanBinding = group.GetVisibilitySetBindingForView(2);

        leftBinding.IsBound.ShouldBeTrue();
        leftBinding.IsImmutable.ShouldBeTrue();
        leftBinding.VisibilitySetIdentity.ShouldBe(RuntimeHelpers.GetHashCode(runtimeVisibleSet));
        leftBinding.ShouldBe(rightBinding);
        leftBinding.ShouldBe(cyclopeanBinding);
    }

    [Test]
    public void ViewRenderGroup_DesktopEditingBindsIndependentDesktopAndVrVisibleSets()
    {
        object desktopVisibleSet = new();
        object vrVisibleSet = new();

        ViewRenderGroupContext group = ViewRenderGroupContext.CreateDesktopEditingGroup(
                EVrViewRenderMode.SequentialViews,
                CreateView(EVrOutputViewKind.DesktopEditor, 0u, Matrix4x4.Identity),
                CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f)),
                CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f)),
                new ViewVisibilityFrustumContext(Matrix4x4.Identity, CreateProjection(70.0f), true, false))
            .BindVisibilitySet(0, desktopVisibleSet, generation: 10UL, debugName: "desktop")
            .BindVisibilitySet(1, vrVisibleSet, generation: 11UL, debugName: "vr-eyes");

        ViewVisibilitySetBinding desktopBinding = group.GetVisibilitySetBindingForView(0);
        ViewVisibilitySetBinding leftBinding = group.GetVisibilitySetBindingForView(1);
        ViewVisibilitySetBinding rightBinding = group.GetVisibilitySetBindingForView(2);

        desktopBinding.VisibilitySetIdentity.ShouldBe(RuntimeHelpers.GetHashCode(desktopVisibleSet));
        leftBinding.VisibilitySetIdentity.ShouldBe(RuntimeHelpers.GetHashCode(vrVisibleSet));
        leftBinding.ShouldBe(rightBinding);
        desktopBinding.ShouldNotBe(leftBinding);
    }

    [Test]
    public void ViewRenderGroup_SinglePassStereoCarriesDistinctPerEyeFovealCenters()
    {
        VrFoveationResolution resolution = VrFoveationResolver.Resolve(
            ERenderLibrary.Vulkan,
            EVrFoveationMode.Fixed,
            EVrFoveationQualityPreset.Balanced,
            requireRequested: true,
            new VrFoveationBackendCapabilities(true, false, false, false, false, false));

        ViewRenderContext left = CreateView(
            EVrOutputViewKind.LeftEye,
            0u,
            Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f),
            foveation: ViewFoveationContext.FromResolution(
                resolution,
                Vector2.Zero,
                new Vector2(-0.08f, 0.0f),
                new Vector2(0.44f, 0.5f),
                EVrFoveationGazeSource.FixedCenter,
                backendResourceKey: 101UL));
        ViewRenderContext right = CreateView(
            EVrOutputViewKind.RightEye,
            1u,
            Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f),
            foveation: ViewFoveationContext.FromResolution(
                resolution,
                Vector2.Zero,
                new Vector2(0.08f, 0.0f),
                new Vector2(0.56f, 0.5f),
                EVrFoveationGazeSource.FixedCenter,
                backendResourceKey: 202UL));
        ViewRenderContext cyclopean = CreateView(EVrOutputViewKind.CyclopeanDesktop, 2u, Matrix4x4.Identity);

        ViewRenderGroupContext group = ViewRenderGroupContext.CreateCombinedRuntimeGroup(
            EVrViewRenderMode.SinglePassStereo,
            left,
            right,
            cyclopean,
            ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(left, right, cyclopean));

        group.RenderMode.ShouldBe(EVrViewRenderMode.SinglePassStereo);
        group.GetView(0).Foveation.RenderTargetUvCenter.ShouldBe(new Vector2(0.44f, 0.5f));
        group.GetView(1).Foveation.RenderTargetUvCenter.ShouldBe(new Vector2(0.56f, 0.5f));
        group.GetView(0).Foveation.BackendResourceKey.ShouldNotBe(group.GetView(1).Foveation.BackendResourceKey);
    }

    [Test]
    public void ViewRenderGroup_ParallelRecordingWorkItemReceivesImmutableFoveationContext()
    {
        VrFoveationResolution resolution = new(
            EVrFoveationMode.EyeTracked,
            EVrFoveationMode.EyeTracked,
            EVrFoveationQualityPreset.Aggressive,
            EVrFoveationCapabilityPath.OpenXrQuadViews,
            true,
            null);
        ViewFoveationContext foveation = ViewFoveationContext.FromResolution(
            resolution,
            new Vector2(0.1f, -0.1f),
            new Vector2(0.04f, -0.02f),
            new Vector2(0.52f, 0.48f),
            EVrFoveationGazeSource.EyeTracked,
            backendResourceKey: 303UL);
        ViewRenderContext left = CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.Identity, foveation: foveation);
        ViewRenderContext right = CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.Identity);
        ViewRenderContext cyclopean = CreateView(EVrOutputViewKind.CyclopeanDesktop, 2u, Matrix4x4.Identity);

        ViewRenderGroupContext group = ViewRenderGroupContext.CreateCombinedRuntimeGroup(
            EVrViewRenderMode.ParallelCommandBufferRecording,
            left,
            right,
            cyclopean,
            ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(left, right, cyclopean));

        ViewRecordingWorkItem workItem = group.CreateRecordingWorkItem(0, workerIndex: 7);

        workItem.RenderMode.ShouldBe(EVrViewRenderMode.ParallelCommandBufferRecording);
        workItem.WorkerIndex.ShouldBe(7);
        workItem.HasImmutableFoveationInput.ShouldBeTrue();
        workItem.Foveation.ShouldBe(foveation);
    }

    [Test]
    public void ViewRenderGroup_FoveationDoesNotShrinkConservativeVisibilityFrustum()
    {
        ViewRenderContext leftOff = CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f));
        ViewRenderContext rightOff = CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f));
        ViewRenderContext cyclopeanOff = CreateView(EVrOutputViewKind.CyclopeanDesktop, 2u, Matrix4x4.Identity);
        ViewVisibilityFrustumContext offVisibility = ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(leftOff, rightOff, cyclopeanOff);

        VrFoveationResolution resolution = new(
            EVrFoveationMode.Fixed,
            EVrFoveationMode.Fixed,
            EVrFoveationQualityPreset.Aggressive,
            EVrFoveationCapabilityPath.VulkanFragmentShadingRate,
            true,
            null);
        ViewFoveationContext aggressive = ViewFoveationContext.FromResolution(
            resolution,
            Vector2.Zero,
            Vector2.Zero,
            new Vector2(0.2f, 0.8f),
            EVrFoveationGazeSource.FixedCenter,
            backendResourceKey: 404UL);
        ViewRenderContext leftFoveated = CreateView(EVrOutputViewKind.LeftEye, 0u, Matrix4x4.CreateTranslation(0.032f, 0.0f, 0.0f), foveation: aggressive);
        ViewRenderContext rightFoveated = CreateView(EVrOutputViewKind.RightEye, 1u, Matrix4x4.CreateTranslation(-0.032f, 0.0f, 0.0f), foveation: aggressive);
        ViewRenderContext cyclopeanFoveated = CreateView(EVrOutputViewKind.CyclopeanDesktop, 2u, Matrix4x4.Identity, foveation: aggressive);
        ViewVisibilityFrustumContext foveatedVisibility = ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(leftFoveated, rightFoveated, cyclopeanFoveated);

        foveatedVisibility.IsConservative.ShouldBeTrue();
        foveatedVisibility.IncludesFoveatedViews.ShouldBeTrue();
        foveatedVisibility.ViewMatrix.ShouldBe(offVisibility.ViewMatrix);
        foveatedVisibility.ProjectionMatrix.ShouldBe(offVisibility.ProjectionMatrix);
    }

    [Test]
    public void RenderFrameViewSet_QuadViewCarriesStableRolesParentsAndVisibility()
    {
        RenderFrameViewSet viewSet = CreateQuadViewSet(EVrViewRenderMode.SequentialViews);

        viewSet.ViewCount.ShouldBe(4);
        viewSet.VisibilityPolicy.ShouldBe(EVrVisibilityPolicy.SharedFrameViewSet);
        viewSet.VisibilityGroupCount.ShouldBe(1);
        viewSet.IsQuadViewSet.ShouldBeTrue();
        viewSet.GetView(0).Kind.ShouldBe(EVrOutputViewKind.LeftWide);
        viewSet.GetView(1).Kind.ShouldBe(EVrOutputViewKind.RightWide);
        viewSet.GetView(2).Kind.ShouldBe(EVrOutputViewKind.LeftInset);
        viewSet.GetView(3).Kind.ShouldBe(EVrOutputViewKind.RightInset);
        viewSet.GetView(2).ParentViewId.ShouldBe(0u);
        viewSet.GetView(3).ParentViewId.ShouldBe(1u);
        viewSet.GetView(0).OpenXrViewIndex.ShouldBe(0);
        viewSet.GetView(3).OpenXrViewIndex.ShouldBe(3);
        viewSet.CountViewsInVisibilityGroup(0).ShouldBe(4);
    }

    [Test]
    public void RenderFrameViewBatchPlanner_SinglePassStereoSplitsQuadViewIntoWideAndInsetPairs()
    {
        RenderFrameViewSet viewSet = CreateQuadViewSet(EVrViewRenderMode.SinglePassStereo);

        RenderFrameViewBatchPlan plan = RenderFrameViewBatchPlanner.Plan(
            viewSet,
            RenderFrameViewBatchCapabilities.VulkanMultiviewStereoPairs);

        plan.BatchCount.ShouldBe(2);

        RenderFrameViewBatch wide = plan.GetBatch(0);
        wide.Kind.ShouldBe(ERenderFrameViewBatchKind.LayeredStereoPair);
        wide.ViewCount.ShouldBe(2);
        wide.ContainsView(0).ShouldBeTrue();
        wide.ContainsView(1).ShouldBeTrue();
        wide.ContainsView(2).ShouldBeFalse();

        RenderFrameViewBatch inset = plan.GetBatch(1);
        inset.Kind.ShouldBe(ERenderFrameViewBatchKind.LayeredStereoPair);
        inset.ViewCount.ShouldBe(2);
        inset.ContainsView(2).ShouldBeTrue();
        inset.ContainsView(3).ShouldBeTrue();
    }

    [Test]
    public void RenderFrameViewBatchPlanner_ParallelRecordingKeepsQuadViewsAsIndependentWork()
    {
        RenderFrameViewSet viewSet = CreateQuadViewSet(EVrViewRenderMode.ParallelCommandBufferRecording);

        RenderFrameViewBatchPlan plan = RenderFrameViewBatchPlanner.Plan(
            viewSet,
            RenderFrameViewBatchCapabilities.VulkanMultiviewStereoPairs);

        plan.BatchCount.ShouldBe(4);
        for (int i = 0; i < plan.BatchCount; i++)
        {
            RenderFrameViewBatch batch = plan.GetBatch(i);
            batch.Kind.ShouldBe(ERenderFrameViewBatchKind.ParallelCommandBufferRecording);
            batch.ViewCount.ShouldBe(1);
            batch.ContainsView(i).ShouldBeTrue();
        }
    }

    [Test]
    public void RenderFrameViewBatchPlanner_QuadViewLayeredBatchRequiresCompatibleExtents()
    {
        RenderFrameViewSet mixedExtentQuad = CreateQuadViewSet(EVrViewRenderMode.SinglePassStereo);

        RenderFrameViewBatchPlan splitPlan = RenderFrameViewBatchPlanner.Plan(
            mixedExtentQuad,
            RenderFrameViewBatchCapabilities.VulkanMultiviewQuadView);

        splitPlan.BatchCount.ShouldBe(2);
        splitPlan.GetBatch(0).Kind.ShouldBe(ERenderFrameViewBatchKind.LayeredStereoPair);
        splitPlan.GetBatch(1).Kind.ShouldBe(ERenderFrameViewBatchKind.LayeredStereoPair);

        RenderFrameViewBatchPlan fullLayerPlan = RenderFrameViewBatchPlanner.Plan(
            mixedExtentQuad,
            RenderFrameViewBatchCapabilities.VulkanMultiviewQuadView with { SupportsMixedLayerExtents = true });

        fullLayerPlan.BatchCount.ShouldBe(1);
        fullLayerPlan.GetBatch(0).Kind.ShouldBe(ERenderFrameViewBatchKind.LayeredViewSet);
        fullLayerPlan.GetBatch(0).ViewCount.ShouldBe(4);
    }

    [Test]
    public void SourceContracts_FrameOutputPacingManifestAndMirrorPolicy()
    {
        string contracts = string.Join("\n", new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Enums/EFrameOutputKind.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Enums/EVrMirrorMode.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Enums/EVrOutputViewKind.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Records/FrameOutputPacingDecision.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Records/FrameOutputTelemetry.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Records/RenderFrameViewSet.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/RenderFrameViewBatchPlanner.cs"),
        });
        string settings = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");
        string stats = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.FrameOutputs.cs");
        string host = ReadWorkspaceFile("XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");
        string viewport = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string timerFrame = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeTimerFrame.cs");
        string window = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profileCapture = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");

        contracts.ShouldContain("enum EFrameOutputKind");
        contracts.ShouldContain("enum EVrMirrorMode");
        contracts.ShouldContain("FrameOutputPacingDecision");
        contracts.ShouldContain("FrameOutputTelemetry");
        contracts.ShouldContain("RenderFrameViewSet");
        contracts.ShouldContain("RenderFrameViewBatchPlanner");
        contracts.ShouldContain("LeftWide");
        contracts.ShouldContain("RightInset");
        settings.ShouldContain("public EVrMirrorMode VrMirrorMode");
        settings.ShouldContain("public float VrCyclopeanDesktopTargetRateHz");
        settings.ShouldContain("public bool VrDesktopAutoSkipWhenOverBudget");
        stats.ShouldContain("IsCadenceFrameDue");
        stats.ShouldContain("VR120");
        host.ShouldContain("EvaluateFrameOutputPacing");
        host.ShouldContain("RecordForcedSkip");
        host.ShouldContain("HasRecentRenderedDesktopOutput");
        viewport.ShouldContain("CollectVisibleAutomatic");
        viewport.ShouldContain("RecordFrameOutput");
        viewport.ShouldContain("AccumulateSkippedSceneRenderDelta");
        viewport.ShouldContain("PushSceneRenderDeltaScope");
        timerFrame.ShouldContain("PushScopedRenderDeltaSeconds");
        window.ShouldContain("TryRenderDesktopMirrorComposition");
        packet.ShouldContain("FrameOutputManifestData");
        profileCapture.ShouldContain("\"frame_outputs\"");
        profileCapture.ShouldContain("ProfileCaptureSchemaVersion = 4");
    }

    [Test]
    public void SourceContracts_SurfaceViewModeAndFoveationSettings()
    {
        string settings = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettings.cs");
        string store = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs");
        string bootstrap = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapRenderSettings.cs");
        string contracts = string.Join("\n", new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/VRRenderingContracts/Enums/EVrViewRenderImplementationPath.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/VRRenderingContracts/Enums/EVrTemporalHistoryPolicy.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/VRRenderingContracts/Enums/EOpenXrEyeResolutionPreset.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Core/Settings/VRRenderingContracts/VrViewRenderModeResolver.cs"),
        });
        string openXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string openXrFrameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string openXrResolution = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Resolution.cs");
        string openXrRuntimeState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs");
        string openXrState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string openXrFoveation = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Foveation.cs");
        string environment = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string smoke = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SmokeDiagnostics.cs");
        string engineVrState = ReadWorkspaceFile("XRENGINE/Engine/Engine.VRState.cs");
        string engineStats = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs");
        string rendererState = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.RendererState.cs");
        string profileCapture = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");
        string schema = ReadWorkspaceFile(".vscode/schemas/unit-testing-world-settings.schema.json");
        string xrViewport = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string defaultPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs");
        string temporalAccumulation = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs");
        string pushViewportRenderArea = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_PushViewportRenderArea.cs");

        settings.ShouldContain("public EVrViewRenderMode ViewRenderMode");
        settings.ShouldContain("public UnitTestingVrFoveationSettings Foveation");
        settings.ShouldContain("public UnitTestingOpenXrEyeResolutionSettings OpenXrEyeResolution");
        settings.ShouldContain("public EOpenXrEyeResolutionPreset Preset");
        settings.ShouldContain("OpenXR Vulkan SinglePassStereo strictly");
        store.ShouldContain("IsJsonPropertyPathSpecified");
        store.ShouldContain("UnitTestVrViewRenderMode");
        store.ShouldContain("UnitTestVrFoveationMode");
        store.ShouldContain("UnitTestOpenXrEyeResolutionPreset");
        store.ShouldContain("ResolveMonadoSimulatedDisplayProfile");
        store.ShouldContain("ApplyMonadoSimulatedDisplayProfileEnvironment");
        store.ShouldContain("MonadoRuntimeRecommendedEyeWidth = 896u");
        store.ShouldContain("Monado RuntimeRecommended OpenXR eye resolution cannot provide an exact preview");
        store.ShouldContain("displayProfile.CompositorScalePercentage");
        store.ShouldContain("CaptureCurrentRuntimeOpenXrEyeResolutionSettings");
        store.ShouldContain("TryStopRunningMonadoService");
        store.ShouldContain("Monado simulated display profile changed");
        environment.ShouldContain("SIMULATED_DISPLAY_WIDTH");
        environment.ShouldContain("SIMULATED_DISPLAY_HEIGHT");
        environment.ShouldContain("XRT_COMPOSITOR_SCALE_PERCENTAGE");
        environment.ShouldContain("OXR_VIEWPORT_SCALE_PERCENTAGE");
        environment.ShouldContain("XRE_OPENXR_EYE_RESOLUTION_PRESET");
        environment.ShouldContain("XRE_OPENXR_EYE_RESOLUTION_SCALE");
        environment.ShouldContain("XRE_OPENXR_EYE_RESOLUTION_WIDTH");
        environment.ShouldContain("XRE_OPENXR_EYE_RESOLUTION_HEIGHT");
        bootstrap.ShouldContain("renderSettings.VrViewRenderMode = settings.VR.ViewRenderMode");
        bootstrap.ShouldContain("renderSettings.EnableVrFoveatedViewSet = settings.VR.Foveation.Mode != EVrFoveationMode.Off");
        bootstrap.ShouldContain("renderSettings.OpenXrEyeResolutionPreset = settings.VR.OpenXrEyeResolution.Preset");
        openXr.ShouldContain("VrViewRenderModeResolver.Resolve");
        openXr.ShouldContain("EVrViewRenderMode.SequentialViews");
        openXr.ShouldContain("GetOpenXrSwapchainWidth");
        openXrResolution.ShouldContain("EOpenXrEyeResolutionPreset.ValveIndex");
        openXrResolution.ShouldContain("EOpenXrEyeResolutionPreset.QuestPro");
        openXrResolution.ShouldContain("EOpenXrEyeResolutionPreset.BigscreenBeyond2");
        openXrResolution.ShouldContain("RecordOpenXrSwapchainExtent");
        openXrResolution.ShouldContain("ExceedsRuntimeMax");
        openXrResolution.ShouldContain("Clamping swapchain extent");
        openXrResolution.ShouldContain("RuntimeEngine.Rendering.SettingsChanged += HandleOpenXrRenderSettingsChanged");
        openXrResolution.ShouldContain("QueueOpenXrEyeResolutionSessionRecreate");
        openXrResolution.ShouldContain("RecreateOpenXrSessionResourcesForEyeResolution");
        openXrResolution.ShouldContain("TearDownSessionResourcesWithCurrentContext(destroyInstance: true)");
        openXrResolution.ShouldContain("RuntimeRenderingHostServices.Current.TryEnsureOpenXrRuntimeService(serviceReason)");
        openXrResolution.ShouldContain("SetRuntimeState(OpenXrRuntimeState.DesktopOnly)");
        openXrRuntimeState.ShouldContain("GetGraphicsDeviceFailureProbeDelay");
        openXrState.ShouldContain("_appliedOpenXrEyeResolutionPreset");
        openXrState.ShouldContain("_openXrEyeResolutionRecreateQueued");
        openXrState.ShouldContain("_intentionalOpenXrRecreateBackoffBypassUntilUtc");
        openXr.ShouldContain("LogOpenXrViewRenderModeResolution");
        openXr.ShouldContain("requested={0} effective={1} backend={2} supported={3} path={4} temporalHistoryPolicy={5} parallelGate={6} swapchainFormats={7} trueStereoMultiviewSupport={8}");
        openXr.ShouldContain("DescribeOpenXrSwapchainFormats");
        openXr.ShouldContain("DescribeOpenXrTrueStereoMultiviewSupport");
        openXr.ShouldContain("EffectiveImplementationPath");
        openXr.ShouldContain("TemporalHistoryPolicy");
        openXr.ShouldContain("OpenXrStereoRenderTarget");
        openXr.ShouldContain("TryRenderVulkanTrueSinglePassStereoToSwapchains");
        openXr.ShouldContain("TryEnsureVulkanStereoRenderTarget");
        openXr.ShouldContain("_openXrStereoViewport ??= new XRViewport(null)");
        openXr.ShouldContain("True SinglePassStereo did not render this frame. Sequential/per-eye fallback is forbidden");
        openXr.ShouldContain("FrameModeMismatch");
        openXr.ShouldNotContain("using OpenXR per-eye swapchain compatibility path");
        openXr.ShouldContain("Strict SinglePassStereo render failed");
        openXr.ShouldContain("requestSequentialFallback = permitSequentialFallback");
        openXr.ShouldContain("stereoViewport.RenderStereo");
        openXr.ShouldContain("stereoViewport.MeshRenderCommandsOverride = null");
        openXr.ShouldNotContain("stereoViewport.MeshRenderCommandsOverride = sharedMeshCommands");
        openXr.ShouldContain("RendersExternalSwapchainTarget: false");
        openXr.ShouldContain("RendersToExternalSwapchainTarget = true");
        openXr.ShouldContain("SkippedResizeCatchUpThisFrame");
        openXrFrameLifecycle.ShouldContain("useTrueSinglePassStereo");
        openXrFrameLifecycle.ShouldContain("EnsureOpenXrStereoViewport");
        openXrFrameLifecycle.ShouldContain("ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo");
        openXrFrameLifecycle.ShouldContain("ReleaseOpenXrStereoViewportPipelineForExternalEyes");
        openXrFrameLifecycle.ShouldContain("ApplyOpenXrEyeCameraRenderSettings");
        openXrFrameLifecycle.ShouldContain("ResolveOpenXrEyeAntiAliasingMode");
        openXrFrameLifecycle.ShouldContain("eyeCamera.AntiAliasingModeOverride = antiAliasingMode");
        openXrFrameLifecycle.ShouldContain("EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain");
        openXrFrameLifecycle.ShouldContain("collectViewport = _openXrStereoViewport");
        openXrFrameLifecycle.ShouldContain("collectViewport.RenderPipeline = leftEyePipeline");
        openXrFrameLifecycle.ShouldContain("stereoViewport.SwapBuffers(stereoMeshCommands");
        openXrFrameLifecycle.ShouldContain("CollectOpenXrEyeVisible");
        openXrFrameLifecycle.ShouldContain("_openXrLeftViewport.SwapBuffers(leftCommands");
        openXrFrameLifecycle.ShouldContain("_openXrRightViewport.SwapBuffers(rightCommands");
        openXrFrameLifecycle.ShouldNotContain("_openXrSharedMeshRenderCommands");
        openXrState.ShouldContain("_openXrStereoViewport");
        openXrState.ShouldContain("_openXrStereoRenderPipeline");
        openXrState.ShouldContain("_pendingXrFrameUsesTrueSinglePassStereo");
        openXrState.ShouldContain("_vulkanStereoColorArray");
        openXrState.ShouldContain("_vulkanStereoDepthArray");
        openXrState.ShouldContain("GetOrCreateOpenXrStereoPipeline");
        xrViewport.ShouldContain("meshRenderCommandsOverride: MeshRenderCommandsOverride");
        pushViewportRenderArea.ShouldContain("UseInternalResolution || vp.RendersToExternalSwapchainTarget");
        contracts.ShouldContain("EVrViewRenderImplementationPath");
        contracts.ShouldContain("EVrTemporalHistoryPolicy");
        contracts.ShouldContain("EOpenXrEyeResolutionPreset");
        contracts.ShouldNotContain("OpenXrSinglePassCompatibility");
        contracts.ShouldContain("DisabledExternalPerEyeSwapchain");
        openXrFoveation.ShouldContain("BuildOpenXrFoveationBackendCapabilities");
        openXrFoveation.ShouldContain("CreateOpenXrEyeFoveationContext");
        openXrFoveation.ShouldContain("BuildOpenXrFoveationResourceKey");
        openXr.ShouldContain("Foveation: CreateOpenXrEyeFoveationContext(0)");
        openXr.ShouldContain("Foveation: CreateOpenXrEyeFoveationContext(1)");
        smoke.ShouldContain("FoveationEffectiveMode");
        smoke.ShouldContain("ViewRenderModeEffective");
        smoke.ShouldContain("ViewRenderImplementationPath");
        smoke.ShouldContain("ViewRenderTemporalHistoryPolicy");
        rendererState.ShouldContain("ActiveVrViewRenderModeRequested");
        rendererState.ShouldContain("ActiveVrViewRenderModeEffective");
        rendererState.ShouldContain("ActiveVrViewRenderImplementationPath");
        rendererState.ShouldContain("ActiveVrTemporalHistoryPolicy");
        profileCapture.ShouldContain("vr_view_render_mode_requested");
        profileCapture.ShouldContain("vr_view_render_mode_effective");
        profileCapture.ShouldContain("vr_view_render_implementation_path");
        profileCapture.ShouldContain("vr_temporal_history_policy");
        schema.ShouldContain("OpenXR Vulkan SinglePassStereo strictly requires true layered multiview rendering");
        schema.ShouldContain("never falls back to per-eye rendering");
        schema.ShouldContain("unavailable capabilities are logged and the XR output is not rendered");

        string vulkanOpenXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        openXr.ShouldContain("if (!trueSinglePassStereo)");
        AssertTrueStereoPublishDoesNotFlipY(openXr, "true stereo left eye swapchain image");
        AssertTrueStereoPublishDoesNotFlipY(openXr, "true stereo right eye swapchain image");
        vulkanOpenXr.ShouldContain("TryBlitTextureArrayLayerToOpenXrSwapchainImage");
        vulkanOpenXr.ShouldContain("BaseArrayLayer = sourceLayer");
        vulkanOpenXr.ShouldContain("RendersExternalSwapchainTarget = true");
        vulkanOpenXr.ShouldContain("CreateOpenXrPrewarmRenderStateTracker(request.Extent)");
        vulkanOpenXr.ShouldContain("ViewFoveationContext Foveation");
        vulkanOpenXr.ShouldContain("FoveationResourceKey: request.Foveation.BackendResourceKey");
        vulkanOpenXr.ShouldContain("FoveationAttachmentKind: request.Foveation.Attachment.Kind");
        vulkanOpenXr.ShouldContain("FoveationAttachmentOwnedByResourcePlanner: request.Foveation.Attachment.OwnedByResourcePlanner");
        vulkanOpenXr.ShouldContain("hash.Add((int)targetContext.FoveationAttachmentKind)");

        engineVrState.ShouldContain("VrViewRenderMode == EVrViewRenderMode.SinglePassStereo");
        engineStats.ShouldContain("VrViewRenderMode == EVrViewRenderMode.SinglePassStereo");
        defaultPipeline.ShouldContain("VPRC_TemporalAccumulationPass.TryUseHistoryBasedVrEffects");
        temporalAccumulation.ShouldContain("VrViewRenderModeResolver.Resolve");
        temporalAccumulation.ShouldContain("EVrTemporalHistoryPolicy.StereoArrayLayer");
        engineVrState.ShouldNotContain("RenderVRSinglePassStereo");
        engineStats.ShouldNotContain("RenderVRSinglePassStereo");
        defaultPipeline.ShouldNotContain("RenderVRSinglePassStereo");
        temporalAccumulation.ShouldNotContain("RenderVRSinglePassStereo");
    }

    private static void AssertTrueStereoPublishDoesNotFlipY(string openXrVulkanSource, string publishLabel)
    {
        int labelIndex = openXrVulkanSource.IndexOf(publishLabel, StringComparison.Ordinal);
        labelIndex.ShouldBeGreaterThanOrEqualTo(0);

        string publishCallTail = openXrVulkanSource.Substring(labelIndex, Math.Min(500, openXrVulkanSource.Length - labelIndex));
        publishCallTail.ShouldContain("flipY: false");
        publishCallTail.ShouldNotContain("flipY: true");
    }

    [Test]
    public void VulkanSinglePassStereo_FinalShaderAndLayeredViewContractsStayWired()
    {
        string compiler = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");
        string imageTexture = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureView = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");

        compiler.ShouldContain("MultiviewNumViewsLayoutDeclarationRegex");
        compiler.ShouldContain("rewrittenSource = RemoveVulkanMultiviewNumViewsLayout(rewrittenSource);");
        compiler.ShouldContain("if (RequiresExtMultiviewDirective(rewrittenSource))");
        compiler.ShouldContain("rewrittenSource = EnsureExtMultiviewDirective(rewrittenSource);");
        compiler.ShouldContain("private static string RemoveVulkanMultiviewNumViewsLayout(string source)");
        compiler.ShouldContain(@"\bnum_views\s*=");

        imageTexture.ShouldContain("NormalizeImageViewTypeForLayerCount(DefaultViewType, ResolvedArrayLayers)");
        imageTexture.ShouldContain("descriptor = NormalizeAttachmentViewKey(descriptor);");
        imageTexture.ShouldContain("ViewType = NormalizeImageViewTypeForLayerCount(descriptor.ViewType, descriptor.LayerCount)");

        textureView.ShouldContain("ResolveViewType(Data.TextureTarget, _arrayLayers)");
        textureView.ShouldContain("ResolveViewType(Data.TextureTarget, subresourceRange.LayerCount)");
    }

    [Test]
    public void DefaultRenderPipelines_RouteStereoFboRendersThroughFullViewportCommandChain()
    {
        string pipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        string pipeline2 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default2/DefaultRenderPipeline2.CommandChain.cs");
        string pipelineLegacy = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        foreach (string source in new[] { pipeline, pipeline2 })
        {
            source.ShouldContain("ifElse.ConditionEvaluator = ShouldUseViewportTargetCommands;");
            source.ShouldContain("private static bool ShouldUseViewportTargetCommands()");
            source.ShouldContain("RuntimeEngine.Rendering.State.IsStereoPass");
            source.ShouldContain("CreateViewportTargetCommands()");
            source.ShouldContain("CreateFBOTargetCommands()");
        }

        pipelineLegacy.ShouldContain("ifElse.ConditionEvaluator = ShouldUseViewportTargetCommands;");
        pipeline.ShouldNotContain("&& RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null;");
        pipeline2.ShouldNotContain("&& RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null;");
    }

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

    private static ViewRenderContext CreateView(
        EVrOutputViewKind kind,
        uint viewIndex,
        Matrix4x4 view,
        Matrix4x4? projection = null,
        ViewFoveationContext? foveation = null)
        => new(kind, viewIndex, -1, view, projection ?? CreateProjection(70.0f), foveation ?? ViewFoveationContext.Off());

    private static RenderFrameViewSet CreateQuadViewSet(EVrViewRenderMode renderMode)
    {
        RenderFrameViewDescriptor[] views =
        [
            CreateFrameView(EVrOutputViewKind.LeftWide, 0u, RenderFrameViewDescriptor.InvalidViewId, 0, 1600u, 1600u),
            CreateFrameView(EVrOutputViewKind.RightWide, 1u, RenderFrameViewDescriptor.InvalidViewId, 1, 1600u, 1600u),
            CreateFrameView(EVrOutputViewKind.LeftInset, 2u, 0u, 2, 1000u, 1000u),
            CreateFrameView(EVrOutputViewKind.RightInset, 3u, 1u, 3, 1000u, 1000u),
        ];

        return RenderFrameViewSet.Create(
            renderMode,
            EVrVisibilityPolicy.SharedFrameViewSet,
            visibilityGroupCount: 1,
            views: views,
            debugName: "quad-view-rvc");
    }

    private static RenderFrameViewDescriptor CreateFrameView(
        EVrOutputViewKind kind,
        uint viewId,
        uint parentViewId,
        int openXrViewIndex,
        uint width,
        uint height)
        => new(
            viewId,
            kind,
            parentViewId,
            0,
            openXrViewIndex,
            viewId,
            RenderFrameViewRect.FromSize(width, height),
            Matrix4x4.Identity,
            CreateProjection(70.0f),
            Matrix4x4.Identity,
            ViewFoveationContext.Off(),
            kind.ToString());

    private static Matrix4x4 CreateProjection(float verticalFovDegrees)
        => Matrix4x4.CreatePerspectiveFieldOfView(
            verticalFovDegrees * MathF.PI / 180.0f,
            16.0f / 9.0f,
            0.1f,
            100.0f);
}
