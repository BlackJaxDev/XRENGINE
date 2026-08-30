using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedRenderPipelineCapabilityTests
{
    internal static readonly AdvancedRenderPipelineCapabilities SupportedCapabilities = new(
        Backend: RuntimeGraphicsApiKind.Vulkan,
        RendererAvailable: true,
        SupportsIntegerRenderTargets: true,
        VisibilityTargetEncoding: EAdvancedVisibilityTargetEncoding.R32G32UInt,
        SupportsComputeShaders: true,
        SupportsStorageBuffers: true,
        IndirectSubmission: EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount,
        TextureIndirection: EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
        Synchronization: EAdvancedSynchronizationMode.VulkanSynchronization2,
        SupportsFrameSlotStorage: true,
        SupportsStereoArrayResources: true,
        ShaderFamily: EAdvancedShaderFamily.VisibilityBuffer,
        SupportsBufferDeviceAddress: true,
        SupportsDescriptorIndexing: true,
        SupportsDescriptorHeap: false,
        SupportsSubgroupOperations: true,
        SupportsMeshShaders: false,
        SupportsAsyncCompute: true,
        SupportsTimelineSemaphores: true);

    [TestCase(false)]
    [TestCase(true)]
    public void CapabilityResolver_AcceptsRequiredMonoAndStereoCapabilities(bool stereo)
    {
        AdvancedRenderPipelineCapabilityResult result =
            AdvancedRenderPipelineCapabilityResolver.Resolve(SupportedCapabilities, stereo);

        result.IsSupported.ShouldBeTrue();
        result.RejectionReason.ShouldBe(EAdvancedRenderPipelineRejectionReason.None);
    }

    [Test]
    public void CapabilityResolver_ReportsDeterministicRequiredFeatureRejections()
    {
        var cases = new[]
        {
            (
                AdvancedRenderPipelineCapabilities.NoRenderer,
                EAdvancedRenderPipelineRejectionReason.RendererUnavailable),
            (
                SupportedCapabilities with { Backend = RuntimeGraphicsApiKind.Unknown },
                EAdvancedRenderPipelineRejectionReason.UnsupportedBackend),
            (
                SupportedCapabilities with { SupportsIntegerRenderTargets = false },
                EAdvancedRenderPipelineRejectionReason.MissingIntegerRenderTargets),
            (
                SupportedCapabilities with { SupportsComputeShaders = false },
                EAdvancedRenderPipelineRejectionReason.MissingComputeShaders),
            (
                SupportedCapabilities with { SupportsStorageBuffers = false },
                EAdvancedRenderPipelineRejectionReason.MissingStorageBuffers),
            (
                SupportedCapabilities with
                {
                    IndirectSubmission = EAdvancedIndirectSubmissionMode.None,
                },
                EAdvancedRenderPipelineRejectionReason.MissingIndirectSubmission),
            (
                SupportedCapabilities with
                {
                    TextureIndirection = EAdvancedTextureIndirectionMode.None,
                },
                EAdvancedRenderPipelineRejectionReason.MissingTextureIndirection),
            (
                SupportedCapabilities with
                {
                    Synchronization = EAdvancedSynchronizationMode.None,
                },
                EAdvancedRenderPipelineRejectionReason.MissingSynchronization),
            (
                SupportedCapabilities with { SupportsFrameSlotStorage = false },
                EAdvancedRenderPipelineRejectionReason.MissingFrameSlotStorage),
            (
                SupportedCapabilities with { SupportsStereoArrayResources = false },
                EAdvancedRenderPipelineRejectionReason.MissingStereoArrayResources),
            (
                SupportedCapabilities with { ShaderFamily = EAdvancedShaderFamily.None },
                EAdvancedRenderPipelineRejectionReason.MissingShaderFamily),
        };

        foreach (var testCase in cases)
        {
            bool stereo =
                testCase.Item2 ==
                EAdvancedRenderPipelineRejectionReason.MissingStereoArrayResources;
            AdvancedRenderPipelineCapabilityResult result =
                AdvancedRenderPipelineCapabilityResolver.Resolve(testCase.Item1, stereo);

            result.IsSupported.ShouldBeFalse(testCase.Item2.ToString());
            result.RejectionReason.ShouldBe(testCase.Item2);
        }
    }

    [Test]
    public void CapabilityResolver_DoesNotRequireStereoArraysForMono()
    {
        AdvancedRenderPipelineCapabilities capabilities =
            SupportedCapabilities with { SupportsStereoArrayResources = false };

        AdvancedRenderPipelineCapabilityResolver.Resolve(capabilities, stereo: false)
            .IsSupported.ShouldBeTrue();
        AdvancedRenderPipelineCapabilityResolver.Resolve(capabilities, stereo: true)
            .RejectionReason.ShouldBe(
                EAdvancedRenderPipelineRejectionReason.MissingStereoArrayResources);
    }

    [Test]
    public void SelectionResolver_ImplementsAllModePolicies()
    {
        AdvancedRenderPipelineSelectionResult disabled =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Disabled,
                AdvancedRenderPipelineCapabilities.NoRenderer,
                stereo: false);
        disabled.EffectiveKind.ShouldBe(ERenderPipelineKind.LegacyDefault);
        disabled.CapabilityEvaluated.ShouldBeFalse();

        AdvancedRenderPipelineSelectionResult available =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Available,
                SupportedCapabilities,
                stereo: true);
        available.EffectiveKind.ShouldBe(ERenderPipelineKind.Advanced);
        available.CapabilityResult.IsSupported.ShouldBeTrue();

        AdvancedRenderPipelineSelectionResult fallback =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Available,
                AdvancedRenderPipelineCapabilities.NoRenderer,
                stereo: false);
        fallback.EffectiveKind.ShouldBe(ERenderPipelineKind.LegacyDefault);
        fallback.CapabilityResult.RejectionReason.ShouldBe(
            EAdvancedRenderPipelineRejectionReason.RendererUnavailable);

        AdvancedRenderPipelineSelectionResult required =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Required,
                AdvancedRenderPipelineCapabilities.NoRenderer,
                stereo: false);
        required.EffectiveKind.ShouldBe(ERenderPipelineKind.None);
        required.RequiresFailure.ShouldBeTrue();

        AdvancedRenderPipelineSelectionResult diagnostic =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Diagnostic,
                SupportedCapabilities,
                stereo: false);
        diagnostic.EffectiveKind.ShouldBe(ERenderPipelineKind.LegacyDefault);
        diagnostic.CapabilityResult.IsSupported.ShouldBeTrue();
    }

    [Test]
    public void SelectionResolver_RejectsUnknownMode()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => AdvancedRenderPipelineSelectionResolver.Resolve(
                (EAdvancedRenderPipelineMode)int.MaxValue,
                SupportedCapabilities,
                stereo: false));
    }

    [Test]
    [NonParallelizable]
    public void StandardPipelineFactory_UsesSelectionResultBeforeConstruction()
    {
        RenderPipeline disabled = EngineRenderingSettingsApplication.NewStandardRenderPipeline(
            RenderPipelineRequest.DesktopScene(stereo: false, outputId: 1UL),
            EAdvancedRenderPipelineMode.Disabled,
            AdvancedRenderPipelineCapabilities.NoRenderer);
        disabled.ShouldBeOfType<DefaultRenderPipeline>();

        RenderPipeline selected = EngineRenderingSettingsApplication.NewStandardRenderPipeline(
            RenderPipelineRequest.DesktopScene(stereo: true, outputId: 1UL),
            EAdvancedRenderPipelineMode.Available,
            SupportedCapabilities);
        AdvancedRenderPipeline advanced = selected.ShouldBeOfType<AdvancedRenderPipeline>();
        advanced.Stereo.ShouldBeTrue();
        advanced.CapabilityResult.IsSupported.ShouldBeTrue();

        RenderPipeline fallback = EngineRenderingSettingsApplication.NewStandardRenderPipeline(
            RenderPipelineRequest.DesktopScene(stereo: false, outputId: 1UL),
            EAdvancedRenderPipelineMode.Available,
            AdvancedRenderPipelineCapabilities.NoRenderer);
        fallback.ShouldBeOfType<DefaultRenderPipeline>();

        RenderPipeline diagnostic = EngineRenderingSettingsApplication.NewStandardRenderPipeline(
            RenderPipelineRequest.DesktopScene(stereo: false, outputId: 1UL),
            EAdvancedRenderPipelineMode.Diagnostic,
            SupportedCapabilities);
        diagnostic.ShouldBeOfType<DefaultRenderPipeline>();

        AdvancedRenderPipelineNotSupportedException exception = Should.Throw<
            AdvancedRenderPipelineNotSupportedException>(
            () => EngineRenderingSettingsApplication.NewStandardRenderPipeline(
                RenderPipelineRequest.DesktopScene(
                    stereo: false,
                    outputId: 1UL),
                EAdvancedRenderPipelineMode.Required,
                AdvancedRenderPipelineCapabilities.NoRenderer));
        exception.SelectionResult.CapabilityResult.RejectionReason.ShouldBe(
            EAdvancedRenderPipelineRejectionReason.RendererUnavailable);
    }

    [Test]
    [NonParallelizable]
    public void RuntimeFactory_UsesRegisteredSelectionPolicy()
    {
        string variable = XREngineEnvironmentVariables.AdvancedRenderPipelineMode;
        string? previousEnvironmentValue = Environment.GetEnvironmentVariable(variable);
        EAdvancedRenderPipelineMode previousMode =
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode;
        ERvcPipelineMode previousRvcMode =
            RuntimeEngine.Rendering.Settings.RvcPipelineMode;

        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            RuntimeEngine.Rendering.Settings.RvcPipelineMode = ERvcPipelineMode.Off;
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode =
                EAdvancedRenderPipelineMode.Diagnostic;
            EngineRenderingSettingsApplication.InitializeSettingsApplicationBoundary();

            RenderPipeline pipeline = RuntimeEngine.Rendering.NewRenderPipeline(stereo: true);

            DefaultRenderPipeline legacy = pipeline.ShouldBeOfType<DefaultRenderPipeline>();
            legacy.Stereo.ShouldBeTrue();
            AdvancedRenderPipelineSelectionResult selection =
                EngineRenderingSettingsApplication.LastAdvancedRenderPipelineSelection;
            selection.RequestedMode.ShouldBe(EAdvancedRenderPipelineMode.Diagnostic);
            selection.CapabilityEvaluated.ShouldBeTrue();
            selection.CapabilityResult.RejectionReason.ShouldBe(
                EAdvancedRenderPipelineRejectionReason.RendererUnavailable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousEnvironmentValue);
            EffectiveSettingsEnvOverrides.ReloadForTests();
            RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode = previousMode;
            RuntimeEngine.Rendering.Settings.RvcPipelineMode = previousRvcMode;
        }
    }
}
