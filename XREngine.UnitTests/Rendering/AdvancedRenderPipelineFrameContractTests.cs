using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedRenderPipelineFrameContractTests
{
    [Test]
    public void CommandChain_ContainsExactlyOneAnnotatedCommandPerStageInOrder()
    {
        AdvancedRenderPipeline pipeline = new();
        IReadOnlyList<AdvancedRenderStageDescriptor> stages =
            AdvancedRenderPipelineFrameContract.OrderedStages;
        IReadOnlyList<ViewportRenderCommand> commands = pipeline.CommandChain.Commands;

        commands.Count.ShouldBe(stages.Count * 4);

        for (int i = 0; i < stages.Count; i++)
        {
            AdvancedRenderStageDescriptor descriptor = stages[i];
            int commandIndex = i * 4;

            commands[commandIndex]
                .ShouldBeOfType<VPRC_Annotation>()
                .Label.ShouldBe(descriptor.GpuLabel);
            commands[commandIndex + 1]
                .ShouldBeOfType<VPRC_GPUTimerBegin>()
                .Label.ShouldBe(descriptor.GpuLabel);
            commands[commandIndex + 2]
                .ShouldBeOfType<VPRC_AdvancedRenderStage>()
                .Stage.ShouldBe(descriptor.Stage);
            commands[commandIndex + 3]
                .ShouldBeOfType<VPRC_GPUTimerEnd>()
                .Label.ShouldBe(descriptor.GpuLabel);
        }
    }

    [Test]
    public void CommandChain_ContainsNoLegacyOpaqueCompositionCommands()
    {
        IReadOnlyList<ViewportRenderCommand> commands =
            new AdvancedRenderPipeline().CommandChain.Commands;

        commands.ShouldNotContain(static command => command is VPRC_RenderMeshesPass);
        commands.ShouldNotContain(static command => command is VPRC_ForwardDepthNormalPrePass);
        commands.ShouldNotContain(static command => command is VPRC_ForwardPlusLightCullingPass);
        commands.ShouldNotContain(static command => command is VPRC_LightCombinePass);
        commands.ShouldNotContain(static command => command is VPRC_ResolveMsaaGBuffer);
    }

    [Test]
    public void PassMetadata_MatchesStageOrderDomainsAndDependencies()
    {
        AdvancedRenderPipeline pipeline = new();
        AdvancedRenderStageDescriptor[] stages =
            [.. AdvancedRenderPipelineFrameContract.OrderedStages];
        RenderPassMetadata[] passes =
            [.. pipeline.PassMetadata.OrderBy(static pass => pass.PassIndex)];

        passes.Length.ShouldBe(stages.Length + 1);

        for (int i = 0; i < stages.Length; i++)
        {
            AdvancedRenderStageDescriptor stage = stages[i];
            RenderPassMetadata pass = passes.First(p => p.PassIndex == (int)stage.Stage);

            pass.PassIndex.ShouldBe((int)stage.Stage);
            pass.Name.ShouldBe(stage.PassName);
            pass.Stage.ShouldBe(stage.RenderGraphStage);
            bool visibilityStage = stage.Stage is
                EAdvancedRenderStage.VisibilityPreparation or
                EAdvancedRenderStage.VisibilityRaster or
                EAdvancedRenderStage.DepthPyramidAndLateVisibility or
                EAdvancedRenderStage.AttributeReconstruction;
            pass.ResourceUsages.Any().ShouldBe(
                visibilityStage,
                stage.Stage.ToString());

            if (i == 0)
                pass.ExplicitDependencies.ShouldBeEmpty();
            else if (stage.Stage == EAdvancedRenderStage.WorkClassification)
                pass.ExplicitDependencies.Count.ShouldBe(1);
            else
                pass.ExplicitDependencies.ShouldBe([(int)stages[i - 1].Stage]);
        }
    }

    [Test]
    public void VisibilityProfiles_DeclareCoreResourcesAndImmutableDebugVariants()
    {
        AdvancedRenderPipeline pipeline = new();
        RenderPipelineResourceProfile baseline = CreateProfile(
            EAntiAliasingMode.None,
            stereo: false,
            featureMask: 0UL);
        RenderPipelineResourceProfile maximal = CreateProfile(
            EAntiAliasingMode.Msaa,
            stereo: true,
            featureMask: ulong.MaxValue);

        RenderPipelineResourceLayout baselineLayout =
            pipeline.BuildResourceLayout(baseline);
        RenderPipelineResourceLayout maximalLayout =
            pipeline.BuildResourceLayout(maximal);
        baselineLayout.ResourcesByName.ShouldContainKey(
            AdvancedVisibilityResourceNames.Identity);
        baselineLayout.ResourcesByName.ShouldNotContainKey(
            AdvancedVisibilityResourceNames.DebugOutput);
        maximalLayout.ResourcesByName.ShouldContainKey(
            AdvancedVisibilityResourceNames.DebugOutput);

        XRRenderPipelineInstance instance = new();
        XRViewport viewport = new(null);
        ulong expectedClassification = ((ulong)AdvancedClassificationResourceFeature.Standard) << 32;
        pipeline.BuildResourceFeatureMaskForGenerationKey(instance, viewport)
            .ShouldBe(
                (ulong)AdvancedVisibilityResourceFeature.Core |
                (ulong)AdvancedReconstructionResourceFeature.Core |
                expectedClassification);
        viewport.ApplyCapturePolicy(RenderCapturePolicy.DiagnosticFbo);
        pipeline.BuildResourceFeatureMaskForGenerationKey(instance, viewport)
            .ShouldBe(
                (ulong)(
                AdvancedVisibilityResourceFeature.Core |
                AdvancedVisibilityResourceFeature.DebugOutput) |
                (ulong)(
                AdvancedReconstructionResourceFeature.Core |
                AdvancedReconstructionResourceFeature.DebugOutput) |
                ((ulong)(
                AdvancedClassificationResourceFeature.Standard |
                AdvancedClassificationResourceFeature.DebugOutput) << 32) |
                (1UL << 40));
    }

    [TestCase(
        RenderPipelineExternalTargetKind.Window,
        ExternalRenderResourceOwnership.Window,
        ExternalRenderResourceSynchronization.FrameBoundary)]
    [TestCase(
        RenderPipelineExternalTargetKind.CallerProvidedFrameBuffer,
        ExternalRenderResourceOwnership.Caller,
        ExternalRenderResourceSynchronization.CallerProvided)]
    [TestCase(
        RenderPipelineExternalTargetKind.ExternalSwapchain,
        ExternalRenderResourceOwnership.XrRuntime,
        ExternalRenderResourceSynchronization.AcquireRelease)]
    public void ResourceLayout_DeclaresOnlyTheExternalOutputBoundary(
        RenderPipelineExternalTargetKind targetKind,
        ExternalRenderResourceOwnership ownership,
        ExternalRenderResourceSynchronization synchronization)
    {
        RenderPipelineResourceProfile profile = CreateProfile(
            EAntiAliasingMode.None,
            stereo: false,
            featureMask: ulong.MaxValue) with
        {
            ExternalTargetKind = targetKind,
        };

        RenderPipelineResourceLayout layout =
            new AdvancedRenderPipeline().BuildResourceLayout(profile);
        ExternalResourceSpec output = layout.OrderedSpecs
            .OfType<ExternalResourceSpec>()
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ExternalResourceSpec>();

        output.Name.ShouldBe(AdvancedRenderPipeline.ExternalOutputResourceName);
        output.Lifetime.ShouldBe(RenderResourceLifetime.External);
        output.ExternalKind.ShouldBe(ExternalRenderResourceKind.FrameBuffer);
        output.Ownership.ShouldBe(ownership);
        output.Synchronization.ShouldBe(synchronization);
    }

    [Test]
    public void Backends_RemainGatedUntilEveryAdvancedPipelineStageIsImplemented()
    {
        string openGl = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Features/AdvancedPipeline/OpenGLRenderer.AdvancedPipelineCapabilities.cs");
        string vulkan = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "EAdvancedShaderFamily.None");

        openGl.ShouldContain("ShaderFamily: EAdvancedShaderFamily.None");
        vulkan.ShouldContain("EAdvancedShaderFamily.None");
        openGl.ShouldNotContain("ShaderFamily: EAdvancedShaderFamily.VisibilityBuffer");
        vulkan.ShouldNotContain("EAdvancedShaderFamily.VisibilityBuffer");
    }

    [Test]
    public void Selection_GatesAnOtherwiseCapableBackendUntilTheFullPipelineExists()
    {
        AdvancedRenderPipelineCapabilities incomplete =
            AdvancedRenderPipelineCapabilityTests.SupportedCapabilities with
            {
                ShaderFamily = EAdvancedShaderFamily.None,
            };

        AdvancedRenderPipelineSelectionResult available =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Available,
                incomplete,
                stereo: false);
        AdvancedRenderPipelineSelectionResult required =
            AdvancedRenderPipelineSelectionResolver.Resolve(
                EAdvancedRenderPipelineMode.Required,
                incomplete,
                stereo: false);

        available.EffectiveKind.ShouldBe(ERenderPipelineKind.LegacyDefault);
        available.CapabilityResult.RejectionReason.ShouldBe(
            EAdvancedRenderPipelineRejectionReason.MissingShaderFamily);
        required.EffectiveKind.ShouldBe(ERenderPipelineKind.None);
        required.RequiresFailure.ShouldBeTrue();
        required.CapabilityResult.RejectionReason.ShouldBe(
            EAdvancedRenderPipelineRejectionReason.MissingShaderFamily);
    }

    private static RenderPipelineResourceProfile CreateProfile(
        EAntiAliasingMode antiAliasingMode,
        bool stereo,
        ulong featureMask)
        => new(
            DisplayWidth: 1920u,
            DisplayHeight: 1080u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: true,
            antiAliasingMode,
            MsaaSampleCount: antiAliasingMode == EAntiAliasingMode.Msaa ? 4u : 1u,
            stereo,
            featureMask,
            ViewCount: stereo ? 2u : 1u);
}
