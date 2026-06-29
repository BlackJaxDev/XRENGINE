using NUnit.Framework;
using Shouldly;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VrViewRenderModeContractTests
{
    [Test]
    public void ViewRenderModeResolver_ExposesBackendSupportMatrix()
    {
        VrViewRenderModeResolver.Resolve(ERenderLibrary.Vulkan, EVrViewRenderMode.SequentialViews).IsSupported.ShouldBeTrue();
        VrViewRenderModeResolver.Resolve(ERenderLibrary.Vulkan, EVrViewRenderMode.SinglePassStereo).IsSupported.ShouldBeTrue();
        VrViewRenderModeResolver.Resolve(ERenderLibrary.Vulkan, EVrViewRenderMode.ParallelCommandBufferRecording).IsSupported.ShouldBeTrue();

        VrViewRenderModeResolver.Resolve(ERenderLibrary.OpenGL, EVrViewRenderMode.SequentialViews).IsSupported.ShouldBeTrue();
        VrViewRenderModeResolver.Resolve(ERenderLibrary.OpenGL, EVrViewRenderMode.SinglePassStereo).IsSupported.ShouldBeTrue();

        VrViewRenderModeResolution openGlParallel =
            VrViewRenderModeResolver.Resolve(ERenderLibrary.OpenGL, EVrViewRenderMode.ParallelCommandBufferRecording);
        openGlParallel.IsSupported.ShouldBeFalse();
        openGlParallel.Diagnostic!.ShouldContain("Vulkan-only");
    }

    [Test]
    public void ViewRenderModeResolver_RejectsParallelWhenStartupGateDisablesIt()
    {
        VrViewRenderModeResolution resolution = VrViewRenderModeResolver.Resolve(
            ERenderLibrary.Vulkan,
            EVrViewRenderMode.ParallelCommandBufferRecording,
            enableOpenXrVulkanParallelRendering: false);

        resolution.IsSupported.ShouldBeFalse();
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
    public void SourceContracts_SurfaceViewModeAndFoveationSettings()
    {
        string settings = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettings.cs");
        string store = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs");
        string bootstrap = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapRenderSettings.cs");
        string openXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string openXrFoveation = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Foveation.cs");
        string smoke = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SmokeDiagnostics.cs");
        string engineVrState = ReadWorkspaceFile("XRENGINE/Engine/Engine.VRState.cs");
        string engineStats = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs");
        string defaultPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs");
        string temporalAccumulation = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs");

        settings.ShouldContain("public EVrViewRenderMode ViewRenderMode");
        settings.ShouldContain("public UnitTestingVrFoveationSettings Foveation");
        store.ShouldContain("IsJsonPropertyPathSpecified");
        store.ShouldContain("UnitTestVrViewRenderMode");
        store.ShouldContain("UnitTestVrFoveationMode");
        bootstrap.ShouldContain("renderSettings.VrViewRenderMode = settings.VR.ViewRenderMode");
        bootstrap.ShouldContain("renderSettings.EnableVrFoveatedViewSet = settings.VR.Foveation.Mode != EVrFoveationMode.Off");
        openXr.ShouldContain("VrViewRenderModeResolver.Resolve");
        openXr.ShouldContain("EVrViewRenderMode.SequentialViews");
        openXrFoveation.ShouldContain("BuildOpenXrFoveationBackendCapabilities");
        openXrFoveation.ShouldContain("CreateOpenXrEyeFoveationContext");
        openXrFoveation.ShouldContain("BuildOpenXrFoveationResourceKey");
        openXr.ShouldContain("Foveation: CreateOpenXrEyeFoveationContext(0)");
        openXr.ShouldContain("Foveation: CreateOpenXrEyeFoveationContext(1)");
        smoke.ShouldContain("FoveationEffectiveMode");
        smoke.ShouldContain("ViewRenderModeEffective");

        string vulkanOpenXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        vulkanOpenXr.ShouldContain("ViewFoveationContext Foveation");
        vulkanOpenXr.ShouldContain("FoveationResourceKey: request.Foveation.BackendResourceKey");
        vulkanOpenXr.ShouldContain("FoveationAttachmentKind: request.Foveation.Attachment.Kind");
        vulkanOpenXr.ShouldContain("FoveationAttachmentOwnedByResourcePlanner: request.Foveation.Attachment.OwnedByResourcePlanner");
        vulkanOpenXr.ShouldContain("hash.Add((int)targetContext.FoveationAttachmentKind)");

        engineVrState.ShouldContain("VrViewRenderMode == EVrViewRenderMode.SinglePassStereo");
        engineStats.ShouldContain("VrViewRenderMode == EVrViewRenderMode.SinglePassStereo");
        defaultPipeline.ShouldContain("VrViewRenderMode != EVrViewRenderMode.SinglePassStereo");
        temporalAccumulation.ShouldContain("VrViewRenderMode != EVrViewRenderMode.SinglePassStereo");
        engineVrState.ShouldNotContain("RenderVRSinglePassStereo");
        engineStats.ShouldNotContain("RenderVRSinglePassStereo");
        defaultPipeline.ShouldNotContain("RenderVRSinglePassStereo");
        temporalAccumulation.ShouldNotContain("RenderVRSinglePassStereo");
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

    private static Matrix4x4 CreateProjection(float verticalFovDegrees)
        => Matrix4x4.CreatePerspectiveFieldOfView(
            verticalFovDegrees * MathF.PI / 180.0f,
            16.0f / 9.0f,
            0.1f,
            100.0f);
}
