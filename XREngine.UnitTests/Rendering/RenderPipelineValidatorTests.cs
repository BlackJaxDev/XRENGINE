using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Pipelines.Validation.Rules;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelineValidatorTests
{
    private static readonly RenderPipelineResourceProfile Profile = RenderPipelineResourceProfile.Empty with
    {
        InternalWidth = 64u,
        InternalHeight = 32u,
        DisplayWidth = 64u,
        DisplayHeight = 32u,
    };

    [Test]
    public void GpuWorkWithoutPass_FlagsUnmappedGpuCommand()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_RenderQuadToFBO>();

        Evaluate(new GpuWorkWithoutPassRule(), Context(root)).ShouldHaveSingleItem().Severity.ShouldBe(ERenderPipelineProblemSeverity.Error);
    }

    [Test]
    public void GpuWorkWithoutPass_IgnoresCommandsWithoutGpuWork()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_Annotation>();

        Evaluate(new GpuWorkWithoutPassRule(), Context(root)).ShouldBeEmpty();
    }

    [Test]
    public void ScenePassNotRegistered_FlagsUnknownPass()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_RenderMeshesPass>().RenderPass = 5;

        List<RenderPipelineProblem> problems = Evaluate(new ScenePassNotRegisteredRule(), Context(root, registered: [0, 1]));

        problems.ShouldHaveSingleItem().Target.ShouldBe(ERenderPipelineProblemTarget.Command);
    }

    [Test]
    public void ScenePassNotRegistered_AcceptsRegisteredPass()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_RenderMeshesPass>().RenderPass = 1;

        Evaluate(new ScenePassNotRegisteredRule(), Context(root, registered: [0, 1])).ShouldBeEmpty();
    }

    [Test]
    public void UnpairedStatePush_FlagsPushWithoutPop()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_PushViewportRenderArea>();

        Evaluate(new UnpairedStatePushRule(), Context(root)).ShouldHaveSingleItem().RuleId.ShouldBe("command.unpaired-push");
    }

    [Test]
    public void UnpairedStatePush_AcceptsUsingScope()
    {
        ViewportRenderCommandContainer root = new();
        using (root.AddUsing<VPRC_PushViewportRenderArea>())
            root.Add<VPRC_Annotation>();

        Evaluate(new UnpairedStatePushRule(), Context(root)).ShouldBeEmpty();
    }

    [Test]
    public void StageRules_LinkConsumersToSchemaStages()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_RenderQuadToFBO>().ConsumedStageKeys = ["bloom"];
        RenderPipelineValidationContext linked = Context(root, schema: Schema("bloom"));

        Evaluate(new StageWithoutConsumerRule(), linked).ShouldBeEmpty();
        Evaluate(new ConsumerWithoutStageRule(), linked).ShouldBeEmpty();
    }

    [Test]
    public void StageWithoutConsumer_FlagsUnreadStage()
    {
        RenderPipelineValidationContext context = Context(new ViewportRenderCommandContainer(), schema: Schema("bloom"));

        RenderPipelineProblem problem = Evaluate(new StageWithoutConsumerRule(), context).ShouldHaveSingleItem();
        problem.Severity.ShouldBe(ERenderPipelineProblemSeverity.Info);
        problem.Name.ShouldBe("bloom");
    }

    [Test]
    public void ConsumerWithoutStage_FlagsUnknownStageKey()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_RenderQuadToFBO>().ConsumedStageKeys = ["missing"];

        Evaluate(new ConsumerWithoutStageRule(), Context(root)).ShouldHaveSingleItem().Severity.ShouldBe(ERenderPipelineProblemSeverity.Warning);
    }

    [Test]
    public void ResourceUsageRules_FlagUndeclaredUnusedAndUnwrittenResources()
    {
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("Read").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Texture("Unused").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Build(Profile);
        RenderPassMetadataCollection passes = new();
        passes.ForPass(0, "Reader")
            .SampleTexture(RenderGraphResourceNames.MakeTexture("Read"))
            .SampleTexture(RenderGraphResourceNames.MakeTexture("Undeclared"));
        RenderPipelineValidationContext context = Context(new ViewportRenderCommandContainer(), layout, passes);

        Evaluate(new UndeclaredResourceReadRule(), context).ShouldHaveSingleItem().Name.ShouldBe("Undeclared");
        Evaluate(new UnusedResourceRule(), context).ShouldHaveSingleItem().Name.ShouldBe("Unused");
        Evaluate(new UnwrittenResourceRule(), context).ShouldHaveSingleItem().Name.ShouldBe("Read");
    }

    [Test]
    public void ResourceUsageRules_AcceptWrittenAndReadResource()
    {
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("Color").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Build(Profile);
        RenderPassMetadataCollection passes = new();
        passes.ForPass(0, "Writer").WriteTexture(RenderGraphResourceNames.MakeTexture("Color"));
        passes.ForPass(1, "Reader").SampleTexture(RenderGraphResourceNames.MakeTexture("Color"));
        RenderPipelineValidationContext context = Context(new ViewportRenderCommandContainer(), layout, passes);

        Evaluate(new UndeclaredResourceReadRule(), context).ShouldBeEmpty();
        Evaluate(new UnusedResourceRule(), context).ShouldBeEmpty();
        Evaluate(new UnwrittenResourceRule(), context).ShouldBeEmpty();
        Evaluate(new TransientReadBeforeWriteRule(), context).ShouldBeEmpty();
    }

    [Test]
    public void TransientReadBeforeWrite_FlagsEarlyReader()
    {
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("Scratch").Lifetime(RenderResourceLifetime.Transient).Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Build(Profile);
        RenderPassMetadataCollection passes = new();
        passes.ForPass(0, "Reader").SampleTexture(RenderGraphResourceNames.MakeTexture("Scratch"));
        passes.ForPass(1, "Writer").WriteTexture(RenderGraphResourceNames.MakeTexture("Scratch"));

        RenderPipelineProblem problem = Evaluate(new TransientReadBeforeWriteRule(), Context(new ViewportRenderCommandContainer(), layout, passes)).ShouldHaveSingleItem();
        problem.PassIndex.ShouldBe(0);
    }

    [Test]
    public void FrameBufferAttachmentMismatch_FlagsDepthOnColorSlotAndMixedSizes()
    {
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("Color").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Texture("Half").Size(RenderResourceSizePolicy.Internal(0.5f)).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Texture("Depth").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Depth24Stencil8).Add()
            .FrameBuffer("Bad").Color(0, "Color").Color(1, "Depth").Color(2, "Half").Add()
            .Build(Profile);

        List<RenderPipelineProblem> problems = Evaluate(new FrameBufferAttachmentMismatchRule(), Context(new ViewportRenderCommandContainer(), layout));

        problems.Count.ShouldBe(2);
        problems.ShouldContain(p => p.Severity == ERenderPipelineProblemSeverity.Error);
        problems.ShouldContain(p => p.Severity == ERenderPipelineProblemSeverity.Warning);
    }

    [Test]
    public void FrameBufferAttachmentMismatch_AcceptsMatchingAttachments()
    {
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("Color").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Texture("Depth").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Depth24Stencil8).Add()
            .FrameBuffer("Good").Color(0, "Color").DepthStencil("Depth").Add()
            .Build(Profile);

        Evaluate(new FrameBufferAttachmentMismatchRule(), Context(new ViewportRenderCommandContainer(), layout)).ShouldBeEmpty();
    }

    [Test]
    public void BackendUnsupportedFormat_FlagsThreeChannelFormatOnVulkanOnly()
    {
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("Rgb").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgb16f).Add()
            .Texture("Rgba").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba16f).Add()
            .Build(Profile);

        Evaluate(new BackendUnsupportedFormatRule(), Context(new ViewportRenderCommandContainer(), layout, backend: RendererBackendId.Vulkan))
            .ShouldHaveSingleItem().Name.ShouldBe("Rgb");
        Evaluate(new BackendUnsupportedFormatRule(), Context(new ViewportRenderCommandContainer(), layout, backend: RendererBackendId.OpenGL))
            .ShouldBeEmpty();
    }

    [Test]
    public void StereoIncompatibleResource_FlagsMonoAttachmentInStereoProfile()
    {
        RenderPipelineResourceProfile stereo = Profile with { Stereo = true, ViewCount = 2u };
        RenderPipelineResourceLayout layout = new RenderPipelineResourceLayoutBuilder(stereo)
            .Texture("Mono").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).Add()
            .Texture("Stereo").Size(RenderResourceSizePolicy.Internal()).SizedFormat(ESizedInternalFormat.Rgba8).StereoCompatible().Add()
            .FrameBuffer("FBO").Color(0, "Mono").Color(1, "Stereo").Add()
            .Build(stereo);

        Evaluate(new StereoIncompatibleResourceRule(), Context(new ViewportRenderCommandContainer(), layout, profile: stereo))
            .ShouldHaveSingleItem().Name.ShouldBe("Mono");
        Evaluate(new StereoIncompatibleResourceRule(), Context(new ViewportRenderCommandContainer(), layout))
            .ShouldBeEmpty();
    }

    [Test]
    public void Validator_SortsErrorsFirst_AndKeepsSourceProblems()
    {
        ViewportRenderCommandContainer root = new();
        root.Add<VPRC_PushViewportRenderArea>();
        root.Add<VPRC_RenderQuadToFBO>();
        RenderPipelineProblem source = new("source.test", ERenderPipelineProblemSeverity.Info, "Source");
        RenderPipelineValidationContext context = Context(root, sourceProblems: [source]);

        RenderPipelineValidationReport report = RenderPipelineValidator.Validate(context);

        report.Problems[0].Severity.ShouldBe(ERenderPipelineProblemSeverity.Error);
        report.Has("source.test").ShouldBeTrue();
        report.Has("command.unpaired-push").ShouldBeTrue();
        report.ErrorCount.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Validator_ReportsFaultyRule_AndRunsTheOthers()
    {
        RenderPipelineValidationReport report = RenderPipelineValidator.Validate(
            Context(new ViewportRenderCommandContainer(), schema: Schema("bloom")),
            [new ThrowingValidationRule(), new StageWithoutConsumerRule()]);

        report.Has("test.throws").ShouldBeTrue();
        report.Has("stage.no-consumer").ShouldBeTrue();
    }

    private static List<RenderPipelineProblem> Evaluate(IRenderPipelineValidationRule rule, RenderPipelineValidationContext context)
    {
        List<RenderPipelineProblem> problems = [];
        rule.Evaluate(context, problems);
        return problems;
    }

    private static RenderPipelineValidationContext Context(
        ViewportRenderCommandContainer root,
        RenderPipelineResourceLayout? layout = null,
        RenderPassMetadataCollection? passes = null,
        RenderPipelinePostProcessSchema? schema = null,
        RendererBackendId? backend = null,
        RenderPipelineResourceProfile? profile = null,
        IReadOnlyCollection<int>? registered = null,
        IReadOnlyList<RenderPipelineProblem>? sourceProblems = null)
    {
        IReadOnlyCollection<RenderPassMetadata> metadata = passes?.Build() ?? [];
        return new RenderPipelineValidationContext(
            null,
            null,
            metadata,
            RenderPipelineStructureSnapshot.Build(1, root, metadata),
            RenderPipelineResourceSnapshot.Build(layout ?? RenderPipelineResourceLayout.Empty, metadata, null),
            schema ?? RenderPipelinePostProcessSchema.Empty,
            backend ?? RendererBackendId.OpenGL,
            profile ?? Profile,
            registered,
            sourceProblems);
    }

    private static RenderPipelinePostProcessSchema Schema(string stageKey)
        => new(
            new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal)
            {
                [stageKey] = new PostProcessStageDescriptor(stageKey, stageKey, [], null),
            },
            []);
}
