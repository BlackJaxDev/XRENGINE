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

        commands.OfType<VPRC_AdvancedRenderStage>().Count().ShouldBe(stages.Count);
        int previousStageIndex = -1;

        for (int i = 0; i < stages.Count; i++)
        {
            AdvancedRenderStageDescriptor descriptor = stages[i];
            commands.OfType<VPRC_Annotation>()
                .Count(command => command.Label == descriptor.GpuLabel)
                .ShouldBe(1);
            commands.OfType<VPRC_GPUTimerBegin>()
                .Count(command => command.Label == descriptor.GpuLabel)
                .ShouldBe(1);
            commands.OfType<VPRC_AdvancedRenderStage>()
                .Count(command => command.Stage == descriptor.Stage)
                .ShouldBe(1);
            commands.OfType<VPRC_GPUTimerEnd>()
                .Count(command => command.Label == descriptor.GpuLabel)
                .ShouldBe(1);

            int annotationIndex = commands
                .Select((command, index) => (command, index))
                .Single(entry => entry.command is VPRC_Annotation annotation &&
                    annotation.Label == descriptor.GpuLabel)
                .index;
            int beginIndex = commands
                .Select((command, index) => (command, index))
                .Single(entry => entry.command is VPRC_GPUTimerBegin begin &&
                    begin.Label == descriptor.GpuLabel)
                .index;
            int stageIndex = commands
                .Select((command, index) => (command, index))
                .Single(entry => entry.command is VPRC_AdvancedRenderStage stage &&
                    stage.Stage == descriptor.Stage)
                .index;
            int endIndex = commands
                .Select((command, index) => (command, index))
                .Single(entry => entry.command is VPRC_GPUTimerEnd end &&
                    end.Label == descriptor.GpuLabel)
                .index;

            annotationIndex.ShouldBeLessThan(beginIndex);
            beginIndex.ShouldBeLessThan(stageIndex);
            stageIndex.ShouldBeLessThan(endIndex);
            previousStageIndex.ShouldBeLessThan(stageIndex);
            previousStageIndex = stageIndex;
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
    public void PassMetadata_DescribesConcretePassesWithResolvedDependencies()
    {
        AdvancedRenderPipeline pipeline = new();
        RenderPassMetadata[] passes =
            [.. pipeline.PassMetadata.OrderBy(static pass => pass.PassIndex)];

        RenderPassMetadata frameBegin = GetPass("Advanced.FrameBegin");
        RenderPassMetadata deformation = GetPass("Advanced.Deformation");
        RenderPassMetadata preparation = GetPass("Advanced.VisibilityPreparation");
        RenderPassMetadata raster = GetPass("Advanced.VisibilityRaster");
        RenderPassMetadata depthPyramid = GetPass("Advanced.DepthPyramidAndLateVisibility");
        RenderPassMetadata lateRaster = GetPass("Advanced.LateVisibilityRaster");
        RenderPassMetadata ambientOcclusion = GetPass("Advanced.AmbientOcclusion");
        RenderPassMetadata classification = GetPass("Advanced.WorkClassification");

        frameBegin.Stage.ShouldBe(ERenderGraphPassStage.Transfer);
        deformation.Stage.ShouldBe(ERenderGraphPassStage.Compute);
        deformation.ExplicitDependencies.ShouldContain(frameBegin.PassIndex);
        preparation.Stage.ShouldBe(ERenderGraphPassStage.Compute);
        preparation.ExplicitDependencies.ShouldContain(deformation.PassIndex);
        preparation.ResourceUsages.ShouldContain(usage =>
            usage.ResourceName == AdvancedVisibilityResourceNames.Candidates);
        raster.Stage.ShouldBe(ERenderGraphPassStage.Graphics);
        raster.ExplicitDependencies.ShouldContain(preparation.PassIndex);
        raster.ResourceUsages.ShouldContain(usage =>
            usage.ResourceName == AdvancedVisibilityResourceNames.Payloads);
        depthPyramid.Stage.ShouldBe(ERenderGraphPassStage.Compute);
        depthPyramid.ExplicitDependencies.ShouldContain(raster.PassIndex);
        depthPyramid.ResourceUsages.ShouldContain(usage =>
            usage.ResourceName == RenderGraphResourceNames.MakeTexture(
                AdvancedVisibilityResourceNames.CurrentDepthPyramid));
        lateRaster.Stage.ShouldBe(ERenderGraphPassStage.Graphics);
        lateRaster.ExplicitDependencies.ShouldContain(depthPyramid.PassIndex);
        ambientOcclusion.Stage.ShouldBe(ERenderGraphPassStage.Compute);
        ambientOcclusion.ExplicitDependencies.ShouldContain(lateRaster.PassIndex);
        classification.Stage.ShouldBe(ERenderGraphPassStage.Compute);
        classification.ExplicitDependencies.ShouldContain(lateRaster.PassIndex);

        RenderPassMetadata GetPass(string name)
            => passes.Single(pass => pass.Name == name);
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
        ExternalResourceSpec[] externalResources =
            [.. layout.OrderedSpecs.OfType<ExternalResourceSpec>()];
        ExternalResourceSpec output = externalResources.Single(
            resource => resource.Name == AdvancedRenderPipeline.ExternalOutputResourceName);

        externalResources
            .Where(resource => resource.Name != AdvancedRenderPipeline.ExternalOutputResourceName)
            .Select(resource => resource.Name)
            .ShouldBe([
                "LightProbeIrradianceArray",
                "LightProbePrefilterArray",
                "LightProbePositions",
                "LightProbeParameters",
                "LightProbeTetrahedra",
                "LightProbeGridCells",
                "LightProbeGridIndices",
            ]);

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

        openGl.ShouldContain("ShaderFamily: _advancedAdmissionReady ? EAdvancedShaderFamily.VisibilityBuffer : EAdvancedShaderFamily.None");
        vulkan.ShouldContain("EAdvancedShaderFamily.None");
        openGl.ShouldNotContain("ShaderFamily: EAdvancedShaderFamily.VisibilityBuffer,");
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
