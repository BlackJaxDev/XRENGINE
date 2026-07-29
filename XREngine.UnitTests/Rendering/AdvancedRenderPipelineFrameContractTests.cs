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

        passes.Length.ShouldBe(stages.Length);

        for (int i = 0; i < stages.Length; i++)
        {
            RenderPassMetadata pass = passes[i];
            AdvancedRenderStageDescriptor stage = stages[i];

            pass.PassIndex.ShouldBe((int)stage.Stage);
            pass.Name.ShouldBe(stage.PassName);
            pass.Stage.ShouldBe(stage.RenderGraphStage);
            pass.ResourceUsages.ShouldBeEmpty();

            if (i == 0)
                pass.ExplicitDependencies.ShouldBeEmpty();
            else
                pass.ExplicitDependencies.ShouldBe([(int)stages[i - 1].Stage]);
        }
    }

    [Test]
    public void InactiveStageProfiles_DeclareNoPipelineOwnedResources()
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

        pipeline.BuildResourceLayout(baseline).OrderedSpecs.ShouldBeEmpty();
        pipeline.BuildResourceLayout(maximal).OrderedSpecs.ShouldBeEmpty();

        XRRenderPipelineInstance instance = new();
        XRViewport viewport = new(null);
        pipeline.BuildResourceFeatureMaskForGenerationKey(instance, viewport)
            .ShouldBe(0UL);
        viewport.ApplyCapturePolicy(RenderCapturePolicy.GenericSceneCapture);
        pipeline.BuildResourceFeatureMaskForGenerationKey(instance, viewport)
            .ShouldBe(0UL);
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
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ExternalResourceSpec>();

        output.Name.ShouldBe(AdvancedRenderPipeline.ExternalOutputResourceName);
        output.Lifetime.ShouldBe(RenderResourceLifetime.External);
        output.ExternalKind.ShouldBe(ExternalRenderResourceKind.FrameBuffer);
        output.Ownership.ShouldBe(ownership);
        output.Synchronization.ShouldBe(synchronization);
    }

    [Test]
    public void Backends_DoNotAdvertiseIncompleteVisibilityShaderFamily()
    {
        string openGl = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/Features/AdvancedPipeline/OpenGLRenderer.AdvancedPipelineCapabilities.cs");
        string vulkan = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "ShaderFamily: EAdvancedShaderFamily.None");

        foreach (string source in new[] { openGl, vulkan })
        {
            source.ShouldContain("ShaderFamily: EAdvancedShaderFamily.None");
            source.ShouldNotContain("ShaderFamily: EAdvancedShaderFamily.VisibilityBuffer");
        }
    }

    [Test]
    public void Selection_GatesAnOtherwiseCapableBackendUntilVisibilityShadersExist()
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
