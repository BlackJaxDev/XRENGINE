using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Pipelines.Validation.Rules;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AuthoredPostProcessStageTests
{
    [Test]
    public void CustomStage_StoresAndReadsValuesByParameterName()
    {
        AuthoredPostProcessStage stage = new()
        {
            Key = "outline",
            DisplayName = "Outline",
            Parameters =
            [
                CreateParameter("Width", PostProcessParameterKind.Float, 2.5f),
                CreateParameter("Tint", PostProcessParameterKind.Vector3, new Vector3(1f, 0.5f, 0f)),
            ],
        };
        RenderPipelinePostProcessSchema schema = BuildSchema(stage);
        PipelinePostProcessState state = new();
        state.BindToSchema(schema);

        state.TryGetStage("outline", out PostProcessStageState? stageState).ShouldBeTrue();
        stageState!.GetValue("Width", 0f).ShouldBe(2.5f);
        stageState.GetValue("Tint", Vector3.Zero).ShouldBe(new Vector3(1f, 0.5f, 0f));
        stageState.TryGetBacking(out PostProcessPropertyBag? bag).ShouldBeTrue();

        stageState.SetValue("Width", 4f);

        stageState.GetValue("Width", 0f).ShouldBe(4f);
        bag!.Get("Width", 0f).ShouldBe(4f);
    }

    [Test]
    public void LibraryStage_UsesTheCommonStageDescription()
    {
        RenderPipelinePostProcessSchema schema = BuildSchema(new AuthoredPostProcessStage
        {
            Key = CommonPostProcessStages.StageKeys.Bloom,
            IsLibraryStage = true,
            Category = "glow",
        });

        schema.TryGetStage(CommonPostProcessStages.StageKeys.Bloom, out PostProcessStageDescriptor? bloom).ShouldBeTrue();
        bloom!.BackingType.ShouldBe(typeof(BloomSettings));
        schema.Categories.ShouldContain(c => c.Key == "glow");
    }

    [Test]
    public void Stages_FollowTheirOrder()
    {
        RenderPipelinePostProcessSchema schema = BuildSchema(
            new AuthoredPostProcessStage { Key = "late", Order = 10, Parameters = [CreateParameter("A", PostProcessParameterKind.Bool, true)] },
            new AuthoredPostProcessStage { Key = "early", Order = 1, Parameters = [CreateParameter("B", PostProcessParameterKind.Int, 3)] });

        schema.Categories.ShouldHaveSingleItem().StageKeys.ShouldBe(["early", "late"]);
    }

    [Test]
    public void Parameter_DefaultRoundTripsEachKind()
    {
        AuthoredPostProcessParameter parameter = new() { Kind = PostProcessParameterKind.Vector4 };
        parameter.SetDefaultValue(new Vector4(1f, 2f, 3f, 4f));
        parameter.GetDefaultValue().ShouldBe(new Vector4(1f, 2f, 3f, 4f));

        parameter.Kind = PostProcessParameterKind.Bool;
        parameter.SetDefaultValue(true);
        parameter.GetDefaultValue().ShouldBe(true);

        parameter.Kind = PostProcessParameterKind.Int;
        parameter.SetDefaultValue(7);
        parameter.GetDefaultValue().ShouldBe(7);
    }

    [Test]
    public void FullscreenSamplerRule_FlagsSamplerWithoutInput()
    {
        CustomRenderPipeline pipeline = new()
        {
            AuthoredResources =
            [
                new AuthoredRenderResource
                {
                    Name = "EdgeQuad",
                    Kind = EAuthoredRenderResourceKind.QuadMaterial,
                    FragmentShader = "Test/Edge.fs",
                    InputTextures = ["SceneColor"],
                },
            ],
        };
        RenderPipelineValidationContext context = CreateContext(pipeline);

        List<RenderPipelineProblem> problems = [];
        new FullscreenSamplerWithoutInputRule(_ => ["SceneColor", "Depth"]).Evaluate(context, problems);
        problems.ShouldHaveSingleItem().Name.ShouldBe("EdgeQuad");

        problems.Clear();
        new FullscreenSamplerWithoutInputRule(_ => ["SceneColor"]).Evaluate(context, problems);
        problems.ShouldBeEmpty();
    }

    [Test]
    public void AuthoredQuadMaterial_DeclaresItsInputsAsDependencies()
    {
        RenderPipelineResourceProfile profile = RenderPipelineResourceProfile.Empty with { InternalWidth = 32u, InternalHeight = 32u };
        RenderPipelineResourceLayoutBuilder builder = new(profile);
        AuthoredRenderResourceFactory.Declare(builder,
        [
            new AuthoredRenderResource { Name = "SceneColor", Format = XREngine.Data.Rendering.ESizedInternalFormat.Rgba8 },
            new AuthoredRenderResource { Name = "EdgeQuad", Kind = EAuthoredRenderResourceKind.QuadMaterial, FragmentShader = "Test/Edge.fs", InputTextures = ["SceneColor"] },
        ], static _ => null);

        builder.Build(profile).TryGet("EdgeQuad", out RenderPipelineResourceSpec? spec).ShouldBeTrue();
        spec.ShouldBeOfType<QuadMaterialSpec>().Dependencies.ShouldBe(["SceneColor"]);
    }

    private static AuthoredPostProcessParameter CreateParameter(string name, PostProcessParameterKind kind, object value)
    {
        AuthoredPostProcessParameter parameter = new() { Name = name, Kind = kind };
        parameter.SetDefaultValue(value);
        return parameter;
    }

    private static RenderPipelinePostProcessSchema BuildSchema(params AuthoredPostProcessStage[] stages)
    {
        RenderPipelinePostProcessSchemaBuilder builder = new();
        AuthoredPostProcessSchema.Describe(builder, stages);
        return builder.Build();
    }

    private static RenderPipelineValidationContext CreateContext(RenderPipeline pipeline)
        => new(
            pipeline,
            null,
            [],
            RenderPipelineStructureSnapshot.Empty,
            RenderPipelineResourceSnapshot.Empty,
            RenderPipelinePostProcessSchema.Empty,
            RendererBackendId.OpenGL,
            RenderPipelineValidationContext.DefaultProfile,
            null);
}
