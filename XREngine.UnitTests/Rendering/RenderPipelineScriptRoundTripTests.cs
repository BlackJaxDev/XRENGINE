using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelineScriptRoundTripTests
{
    private static readonly RenderPipelineResourceProfile Profile = RenderPipelineResourceProfile.Empty with
    {
        InternalWidth = 128u,
        InternalHeight = 64u,
        DisplayWidth = 128u,
        DisplayHeight = 64u,
    };

    /// <summary>Writes, loads, and writes again, and returns the loaded pipeline. Both texts must be the same.</summary>
    private static RenderPipeline AssertRoundTrip(RenderPipeline pipeline, out string text)
    {
        text = RenderPipelineScriptWriter.Write(pipeline);
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load(text);
        loaded.Problems.ShouldBeEmpty(string.Join(Environment.NewLine, loaded.Problems));
        loaded.Pipeline.ShouldNotBeNull();
        RenderPipelineScriptWriter.Write(loaded.Pipeline).ShouldBe(text);
        return loaded.Pipeline;
    }

    [Test]
    public void DefaultPipeline_WithChangedSettings_RoundTripsAsDerivedScript()
    {
        DefaultRenderPipeline pipeline = new(stereo: false)
        {
            EnableDeferredMsaa = false,
            ForwardDepthPrePassEnabled = !new DefaultRenderPipeline().ForwardDepthPrePassEnabled,
        };

        RenderPipeline loaded = AssertRoundTrip(pipeline, out string text);

        text.ShouldContain("derive Default;");
        text.ShouldContain("EnableDeferredMsaa = false;");
        DefaultRenderPipeline restored = loaded.ShouldBeOfType<DefaultRenderPipeline>();
        restored.EnableDeferredMsaa.ShouldBeFalse();
        restored.ForwardDepthPrePassEnabled.ShouldBe(pipeline.ForwardDepthPrePassEnabled);
    }

    [Test]
    public void DefaultPipeline_WithDefaultSettings_WritesOnlyTheHeader()
    {
        string text = RenderPipelineScriptWriter.Write(new DefaultRenderPipeline());

        text.ShouldNotContain("settings");
        text.ShouldContain("derive Default;");
    }

    [Test]
    public void AdvancedPipeline_KeepsTheStereoArgument()
    {
        RenderPipeline loaded = AssertRoundTrip(new AdvancedRenderPipeline(stereo: true), out string text);

        text.ShouldContain("derive Advanced(stereo: true);");
        loaded.ShouldBeOfType<AdvancedRenderPipeline>().Stereo.ShouldBeTrue();
    }

    [Test]
    public void RvcPipeline_KeepsItsArguments()
    {
        ERvcPipelineMode mode = Enum.GetValues<ERvcPipelineMode>().First(static value => value != ERvcPipelineMode.ForwardPlusOracle);
        RenderPipeline loaded = AssertRoundTrip(new RvcRenderPipeline(stereo: true, mode), out _);

        RvcRenderPipeline rvc = loaded.ShouldBeOfType<RvcRenderPipeline>();
        rvc.Stereo.ShouldBeTrue();
        rvc.RvcPipelineMode.ShouldBe(mode);
    }

    [Test]
    public void AuthoredPipeline_RoundTripsEveryAuthoredField()
    {
        CustomRenderPipeline pipeline = CreateAuthoredPipeline();

        RenderPipeline loaded = AssertRoundTrip(pipeline, out string text);

        CustomRenderPipeline restored = loaded.ShouldBeOfType<CustomRenderPipeline>();
        restored.AuthoredResources.Count.ShouldBe(2);
        restored.AuthoredResources[0].Size.SizeClass.ShouldBe(RenderResourceSizeClass.WindowResolution);
        restored.AuthoredResources[0].Size.ScaleX.ShouldBe(0.5f);
        restored.AuthoredResources[1].Attachments.Single().ResourceName.ShouldBe("Color");
        restored.AuthoredStages.Count.ShouldBe(2);
        restored.AuthoredStages[1].Parameters.Single().Max.ShouldBe(4f);
        restored.RenderPasses!.Keys.ShouldBe([0, 1], ignoreOrder: true);
        restored.RenderPasses[0].ShouldBeOfType<NearToFarRenderCommandSorter>();
        restored.PassDependencies.Single().DependsOnPassIndex.ShouldBe(0);
        text.ShouldContain("if (var UseBloom == 1) as \"Bloom\"");
        text.ShouldContain("if (stage \"my.glow\" enabled)");
    }

    [Test]
    public void AuthoredPipeline_KeepsPushAndPopPairs()
    {
        RenderPipeline loaded = AssertRoundTrip(CreateAuthoredPipeline(), out _);

        ViewportRenderCommandContainer commands = loaded.ShouldBeOfType<CustomRenderPipeline>().Commands.ShouldNotBeNull();
        VPRC_BindFBOByName bind = commands.OfType<VPRC_BindFBOByName>().Single();
        commands.IndexOf(bind.PopCommand).ShouldBeGreaterThan(commands.IndexOf(bind));
    }

    [Test]
    public void AuthoredPipeline_KeepsDataConditions()
    {
        RenderPipeline loaded = AssertRoundTrip(CreateAuthoredPipeline(), out _);

        ViewportRenderCommandContainer commands = loaded.ShouldBeOfType<CustomRenderPipeline>().Commands.ShouldNotBeNull();
        VPRC_IfElse[] branches = [.. commands.OfType<VPRC_IfElse>()];
        branches[0].Label.ShouldBe("Bloom");
        branches[0].Condition.ShouldNotBeNull().Source.ShouldBe(ERenderBranchConditionSource.PipelineVariable);
        branches[0].Condition!.Operator.ShouldBe(ERenderBranchConditionOperator.Equal);
        branches[1].Condition.ShouldNotBeNull().Operator.ShouldBe(ERenderBranchConditionOperator.IsFalse);
        branches[1].Condition!.Source.ShouldBe(ERenderBranchConditionSource.Capability);
        branches[2].Condition.ShouldNotBeNull().Key.ShouldBe("my.glow");
    }

    [Test]
    public void ExpandedDefaultPipeline_LoadsAndListsUnconvertedItems()
    {
        List<string> unconverted = [];
        string expanded = RenderPipelineScriptWriter.WriteExpanded(new DefaultRenderPipeline(), Profile, unconverted);

        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load(expanded);

        loaded.Succeeded.ShouldBeTrue(string.Join(Environment.NewLine, loaded.Problems));
        unconverted.ShouldNotBeEmpty();
        loaded.Document.UnconvertedItems.Count.ShouldBe(unconverted.Count);
        loaded.Document.HasBlockingErrors.ShouldBeFalse();

        string written = RenderPipelineScriptWriter.Write(loaded.Pipeline!);
        RenderPipelineScriptWriter.Write(RenderPipelineScriptLoader.Load(written).Pipeline!).ShouldBe(written);
    }

    [Test]
    public void DerivedScript_WithParentScript_AppliesParentAndChildSettings()
    {
        const string parentText = """
            derive Default;
            settings {
                EnableDeferredMsaa = false;
            }
            """;
        const string childText = """
            derive "parent.xrs";
            settings {
                UseProbeGridAcceleration = false;
            }
            """;
        string root = Path.GetFullPath("pipelines");
        string parentPath = Path.Combine(root, "parent.xrs");

        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load(
            childText,
            Path.Combine(root, "child.xrs"),
            path => string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase) ? parentText : null);

        loaded.Problems.ShouldBeEmpty(string.Join(Environment.NewLine, loaded.Problems));
        DefaultRenderPipeline pipeline = loaded.Pipeline.ShouldBeOfType<DefaultRenderPipeline>();
        pipeline.EnableDeferredMsaa.ShouldBeFalse();
        pipeline.UseProbeGridAcceleration.ShouldBeFalse();

        // The child keeps its parent path and writes only its own settings.
        string written = RenderPipelineScriptWriter.Write(pipeline);
        written.ShouldContain("derive \"parent.xrs\";");
        written.ShouldContain("UseProbeGridAcceleration = false;");
        written.ShouldNotContain("EnableDeferredMsaa");
    }

    [Test]
    public void DerivedScript_ThatReturnsToItself_ReportsTheCycle()
    {
        string root = Path.GetFullPath("pipelines");
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load(
            "derive \"self.xrs\";",
            Path.Combine(root, "self.xrs"),
            _ => "derive \"self.xrs\";");

        loaded.Succeeded.ShouldBeFalse();
        loaded.Problems.ShouldContain(problem => problem.Message.Contains("returns to", StringComparison.Ordinal));
    }

    [Test]
    public void UnknownBase_ReportsLineAndColumn()
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load("// header\nderive Missing;");

        loaded.Succeeded.ShouldBeFalse();
        RenderPipelineScriptProblem problem = loaded.Problems.Single();
        problem.Line.ShouldBe(2);
        problem.Column.ShouldBe(8);
        problem.Message.ShouldContain("Missing");
    }

    [Test]
    public void MistypedBaseArgument_ReportsLineAndColumn()
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load("derive Default(stereo: \"yes\");");

        loaded.Succeeded.ShouldBeFalse();
        RenderPipelineScriptProblem problem = loaded.Problems.Single();
        problem.Line.ShouldBe(1);
        problem.Column.ShouldBe(16);
        problem.Message.ShouldContain("stereo");
    }

    [Test]
    public void UnknownSetting_ReportsLineAndColumn()
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load("derive Default;\nsettings {\n    NotASetting = 1;\n}");

        loaded.Succeeded.ShouldBeFalse();
        RenderPipelineScriptProblem problem = loaded.Problems.Single();
        problem.Line.ShouldBe(3);
        problem.Column.ShouldBe(5);
    }

    [Test]
    public void DerivedScript_WithCommands_IsRejected()
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load("derive Default;\nannotation(Label=\"x\");");

        loaded.Succeeded.ShouldBeFalse();
        loaded.Problems.ShouldContain(problem => problem.Message.Contains("patch statements", StringComparison.Ordinal));
    }

    [Test]
    public void SyntaxError_IsAProblemAndNotAnException()
    {
        RenderPipelineScriptDocument document = RenderPipelineScriptDocument.Parse("resources {\n    Texture Color(Format=;\n}");

        document.HasErrors.ShouldBeTrue();
        document.Problems[0].Line.ShouldBe(2);
    }

    [Test]
    public void ScriptBaseAttributes_MatchTheRegistry()
    {
        Type[] attributed = [.. typeof(RenderPipeline).Assembly.GetTypes()
            .Where(static type => type.GetCustomAttribute<RenderPipelineScriptBaseAttribute>() is not null)];

        foreach (Type type in attributed)
        {
            string name = type.GetCustomAttribute<RenderPipelineScriptBaseAttribute>()!.ScriptName;
            RenderPipelineScriptBases.TryGet(name, out RenderPipelineScriptBase? entry).ShouldBeTrue(name);
            entry!.PipelineType.ShouldBe(type);
        }

        foreach (RenderPipelineScriptBase entry in RenderPipelineScriptBases.All)
            attributed.ShouldContain(entry.PipelineType, entry.Name);
    }

    private static CustomRenderPipeline CreateAuthoredPipeline()
    {
        CustomRenderPipeline pipeline = new()
        {
            AuthoredResources =
            [
                new AuthoredRenderResource
                {
                    Name = "Color",
                    Kind = EAuthoredRenderResourceKind.Texture,
                    Size = AuthoredSizePolicy.Window(0.5f),
                    Format = ESizedInternalFormat.Rgba8,
                },
                new AuthoredRenderResource
                {
                    Name = "ColorFBO",
                    Kind = EAuthoredRenderResourceKind.FrameBuffer,
                    Attachments = [new AuthoredAttachment { ResourceName = "Color" }],
                },
            ],
            AuthoredStages =
            [
                new AuthoredPostProcessStage { Key = AuthoredPostProcessSchema.LibraryStageKeys[0], IsLibraryStage = true },
                new AuthoredPostProcessStage
                {
                    Key = "my.glow",
                    DisplayName = "Glow",
                    Category = "effects",
                    Order = 1,
                    Parameters = [new AuthoredPostProcessParameter { Name = "Strength", Kind = PostProcessParameterKind.Float, Min = 0f, Max = 4f }],
                },
            ],
            RenderPasses = new Dictionary<int, IComparer<RenderCommand>?>
            {
                [0] = new NearToFarRenderCommandSorter(),
                [1] = null,
            },
            PassDependencies = [new AuthoredPassDependency { PassIndex = 1, DependsOnPassIndex = 0 }],
        };

        ViewportRenderCommandContainer commands = new(pipeline);
        VPRC_BindFBOByName bind = new() { FrameBufferName = "ColorFBO" };
        commands.Add(bind);
        commands.Add(new VPRC_Annotation { Label = "Inside" });
        commands.Add(bind.PopCommand);
        commands.Add(new VPRC_IfElse
        {
            Label = "Bloom",
            Condition = RenderBranchCondition.Variable("UseBloom", ERenderBranchConditionOperator.Equal, 1.0),
            TrueCommands = CreateBranch(pipeline, "Bloom on"),
            FalseCommands = CreateBranch(pipeline, "Bloom off"),
        });
        commands.Add(new VPRC_IfElse
        {
            Condition = RenderBranchCondition.Capability(ERenderBranchCapability.StereoPass, expected: false),
            TrueCommands = CreateBranch(pipeline, "Mono"),
        });
        commands.Add(new VPRC_IfElse
        {
            Condition = RenderBranchCondition.StageEnabled("my.glow"),
            TrueCommands = CreateBranch(pipeline, "Glow"),
        });
        pipeline.Commands = commands;
        return pipeline;
    }

    private static ViewportRenderCommandContainer CreateBranch(RenderPipeline pipeline, string label)
    {
        ViewportRenderCommandContainer container = new(pipeline);
        container.Add(new VPRC_Annotation { Label = label });
        return container;
    }
}
