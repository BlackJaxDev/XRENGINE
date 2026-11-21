using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Overlays;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelineOverlayTests
{
    private static readonly string Root = Path.GetFullPath("overlay-pipelines");
    private static readonly string BasePath = Path.Combine(Root, "base.xrs");
    private static readonly string ChildPath = Path.Combine(Root, "child.xrs");

    private static IEnumerable<string> BuiltInPipelines => ["Default", "DefaultStereo", "Advanced", "AdvancedStereo", "RvcStereo"];

    private static RenderPipeline CreateBuiltIn(string name)
        => name switch
        {
            "Default" => new DefaultRenderPipeline(stereo: false),
            "DefaultStereo" => new DefaultRenderPipeline(stereo: true),
            "Advanced" => new AdvancedRenderPipeline(stereo: false),
            "AdvancedStereo" => new AdvancedRenderPipeline(stereo: true),
            _ => new RvcRenderPipeline(stereo: true),
        };

    [TestCaseSource(nameof(BuiltInPipelines))]
    public void BuiltInPipeline_AnchorIdsAreUnique(string name)
    {
        RenderPipeline pipeline = CreateBuiltIn(name);
        List<string> anchors = [];
        CollectAnchors(pipeline.CommandChain, anchors);

        anchors.ShouldNotBeEmpty();
        anchors.GroupBy(static id => id, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ShouldBeEmpty();
    }

    [Test]
    public void DefaultPipeline_HasTheDocumentedAnchors()
    {
        List<string> anchors = [];
        CollectAnchors(new DefaultRenderPipeline().CommandChain, anchors);

        anchors.ShouldContain("post.bloom");
        anchors.ShouldContain("post.temporal");
        anchors.ShouldContain("post.final");
        anchors.ShouldContain("default.transparency");
    }

    [Test]
    public void CommandPatches_ApplyEachKindToAScriptedBase()
    {
        const string child = """
            derive "base.xrs";
            before(base.first) {
                annotation(Label="Before first");
            }
            after(base.first) {
                annotation(Label="After first");
            }
            replace(base.second) {
                annotation(Label="Replaced");
            }
            remove(base.third);
            wrap(base.fourth) if (var UseFour == 1) as "Four";
            set(base.fifth).Label = "Renamed";
            condition(base.bloom) = (var UseBloom == 2);
            append(base.bloom) {
                annotation(Label="Appended");
            }
            """;

        RenderPipeline pipeline = LoadChild(child);

        pipeline.IsDerived.ShouldBeTrue();
        pipeline.OverlayReport.HasErrors.ShouldBeFalse();
        List<string> labels = TopLevelLabels(pipeline.CommandChain);
        labels.ShouldBe(["Before first", "First", "After first", "Replaced", "Four", "Renamed", "Bloom"]);

        VPRC_IfElse wrap = pipeline.CommandChain.OfType<VPRC_IfElse>().Single(static c => c.Label == "Four");
        wrap.Condition.ShouldNotBeNull().Key.ShouldBe("UseFour");
        wrap.TrueCommands.ShouldNotBeNull().Count.ShouldBe(1);

        VPRC_IfElse bloom = pipeline.CommandChain.OfType<VPRC_IfElse>().Single(static c => c.Label == "Bloom");
        bloom.Condition.ShouldNotBeNull().Value.ShouldBe(2.0);
        TopLevelLabels(bloom.TrueCommands.ShouldNotBeNull()).ShouldBe(["Bloom on", "Appended"]);
        pipeline.OverlayReport.Markers.Count.ShouldBeGreaterThan(0);
        pipeline.OverlayReport.RemovedItems.Count.ShouldBe(2);
    }

    [Test]
    public void MissingAnchor_BlocksTheOverlayAndTheBaseRenders()
    {
        const string child = """
            derive "base.xrs";
            after(base.first) {
                annotation(Label="After first");
            }
            remove(base.missing);
            """;

        RenderPipelineScriptLoadResult loaded = Load(child);

        loaded.Problems.ShouldContain(static p => p.IsError && p.Message.Contains("base.missing", StringComparison.Ordinal));
        RenderPipeline pipeline = loaded.Pipeline.ShouldNotBeNull();
        pipeline.OverlayReport.CommandsApplied.ShouldBeFalse();
        TopLevelLabels(pipeline.CommandChain).ShouldNotContain("After first");
        TopLevelLabels(pipeline.CommandChain).ShouldContain("Third");
    }

    [Test]
    public void BodyError_ReportsTheLineInTheScript()
    {
        const string child = "derive \"base.xrs\";\nafter(base.first) {\n    annotation(Label=\"x\");\n    notACommand();\n}\n";

        RenderPipelineScriptLoadResult loaded = Load(child);

        loaded.Succeeded.ShouldBeFalse();
        loaded.Problems.ShouldContain(static p => p.IsError && p.Line == 4);
    }

    [Test]
    public void ResourceAndSchemaPatches_Apply()
    {
        const string child = """
            derive "base.xrs";
            resources {
                Texture Extra(Format=Rgba16f);
                override Texture Color(Format=Rgba16f);
                remove Unused;
            }
            schema {
                stage "my.extra"(DisplayName="Extra") {
                    param Amount(Kind=Float, Min=0, Max=1);
                }
                default "my.glow".Strength = 2;
                category "my.glow" = other;
            }
            """;

        RenderPipeline pipeline = LoadChild(child);

        pipeline.OverlayReport.HasErrors.ShouldBeFalse();
        RenderPipelineResourceLayout layout = pipeline.DescribeResourceLayout(XREngine.Rendering.Pipelines.Validation.RenderPipelineValidationContext.DefaultProfile);
        layout.TryGet("Extra", out _).ShouldBeTrue();
        layout.TryGet("Unused", out _).ShouldBeFalse();
        RenderPipelinePostProcessSchema schema = pipeline.PostProcessSchema;
        schema.StagesByKey.ShouldContainKey("my.extra");
        schema.StagesByKey["my.glow"].Parameters.Single(static p => p.Name == "Strength").DefaultValue.ShouldBe(2f);
    }

    [Test]
    public void HideStage_RemovesTheStage()
    {
        RenderPipeline pipeline = LoadChild("derive \"base.xrs\";\nschema {\n    hide \"my.glow\";\n}\n");

        pipeline.PostProcessSchema.StagesByKey.ShouldNotContainKey("my.glow");
    }

    [Test]
    public void DerivedScript_WithEveryPatchKind_RoundTrips()
    {
        const string child = """
            derive "base.xrs";
            settings {
            }
            before(base.first) {
                annotation(Label="Before first");
            }
            replace(base.second) {
                annotation(Label="Replaced");
            }
            remove(base.third);
            wrap(base.fourth) if (var UseFour == 1) as "Four";
            wrap(base.fifth) {
                bindFBOByName(FrameBufferName="ColorFBO");
            }
            set(base.fifth).Label = "Renamed";
            condition(base.bloom) = (var UseBloom == 2);
            append(base.bloom) {
                annotation(Label="Appended");
            }
            resources {
                Texture Extra(Format=Rgba16f);
                remove Unused;
            }
            schema {
                stage "my.extra"(DisplayName="Extra");
                hide "my.glow";
            }
            """;

        RenderPipeline pipeline = LoadChild(child);
        string written = RenderPipelineScriptWriter.Write(pipeline);

        written.ShouldContain("derive \"base.xrs\";");
        written.ShouldContain("remove(base.third);");
        written.ShouldContain("wrap(base.fourth) if (var UseFour == 1) as \"Four\";");
        written.ShouldContain("set(base.fifth).Label = \"Renamed\";");
        written.ShouldContain("// base-hash:");

        RenderPipelineScriptLoadResult reloaded = Load(written);
        reloaded.Problems.ShouldBeEmpty(string.Join(Environment.NewLine, reloaded.Problems));
        RenderPipelineScriptWriter.Write(reloaded.Pipeline.ShouldNotBeNull()).ShouldBe(written);
    }

    [Test]
    public void ChangedBase_ReportsARebaseWarning()
    {
        RenderPipeline pipeline = LoadChild("derive \"base.xrs\";\nremove(base.third);\n");
        string written = RenderPipelineScriptWriter.Write(pipeline);

        CustomRenderPipeline changed = CreateBase();
        changed.Commands!.Add(new VPRC_Annotation { Label = "New", AnchorId = "base.new" });
        string changedBase = RenderPipelineScriptWriter.Write(changed);
        RenderPipelineScriptLoadResult loaded = Load(written, changedBase);

        loaded.Pipeline.ShouldNotBeNull();
        loaded.Problems.ShouldContain(static p => !p.IsError && p.Message.Contains("rebase", StringComparison.Ordinal));
    }

    [Test]
    public void BuiltInBase_AcceptsAnchorPatches()
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load("""
            derive Default;
            after(post.bloom) {
                annotation(Label="After bloom");
            }
            """);

        loaded.Problems.Where(static p => p.IsError).ShouldBeEmpty(string.Join(Environment.NewLine, loaded.Problems));
        DefaultRenderPipeline pipeline = loaded.Pipeline.ShouldBeOfType<DefaultRenderPipeline>();
        pipeline.OverlayReport.CommandsApplied.ShouldBeTrue();
        RenderCommandPaths.TryFindAnchor(pipeline.CommandChain, "overlay0.patch0.cmd0", out _, out _).ShouldBeTrue();

        string written = RenderPipelineScriptWriter.Write(pipeline);
        written.ShouldContain("after(post.bloom) {");
        RenderPipelineScriptWriter.Write(RenderPipelineScriptLoader.Load(written).Pipeline.ShouldNotBeNull()).ShouldBe(written);
    }

    [Test]
    public void BuiltInBase_AcceptsSchemaPatches()
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load("""
            derive Default;
            schema {
                default bloom.Intensity = 0.5;
                hide motionBlur;
                category bloom = lens;
            }
            """);

        loaded.Problems.Where(static p => p.IsError).ShouldBeEmpty(string.Join(Environment.NewLine, loaded.Problems));
        RenderPipelinePostProcessSchema schema = loaded.Pipeline.ShouldNotBeNull().PostProcessSchema;
        schema.StagesByKey.ShouldNotContainKey("motionBlur");
        schema.StagesByKey["bloom"].Parameters.Single(static p => p.Name == "Intensity").DefaultValue.ShouldBe(0.5f);
    }

    [Test]
    public void BranchAnchor_RoundTripsInAStandaloneScript()
    {
        string text = RenderPipelineScriptWriter.Write(CreateBase());

        text.ShouldContain("as \"Bloom\" anchor base.bloom {");
        RenderPipeline loaded = RenderPipelineScriptLoader.Load(text).Pipeline.ShouldNotBeNull();
        RenderCommandPaths.TryFindAnchor(loaded.CommandChain, "base.bloom", out _, out _).ShouldBeTrue();
    }

    [Test]
    public void VrEyeFactory_AttachesTheScriptOverlays()
    {
        string path = Path.Combine(Path.GetTempPath(), string.Concat("xrs-vr-", Guid.NewGuid().ToString("N"), ".xrs"));
        File.WriteAllText(path, "derive Default;\nafter(post.bloom) {\n    annotation(Label=\"Eye\");\n}\n");
        try
        {
            RenderPipeline pipeline = OpenXrEyeRenderPipelineFactory.Create(false, EVrRenderPipeline.Default, ERvcPipelineMode.ForwardPlusOracle, path);

            pipeline.IsDerived.ShouldBeTrue();
            pipeline.OverlayReport.HasErrors.ShouldBeFalse();
            OpenXrEyeRenderPipelineFactory.Matches(pipeline, false, EVrRenderPipeline.Default, ERvcPipelineMode.ForwardPlusOracle, path).ShouldBeTrue();
            OpenXrEyeRenderPipelineFactory.Matches(pipeline, false, EVrRenderPipeline.Default, ERvcPipelineMode.ForwardPlusOracle).ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void VrEyeFactory_RejectsAScriptWithADifferentBase()
    {
        string path = Path.Combine(Path.GetTempPath(), string.Concat("xrs-vr-", Guid.NewGuid().ToString("N"), ".xrs"));
        File.WriteAllText(path, "derive Advanced;\nsettings {\n}\n");
        try
        {
            Should.Throw<NotSupportedException>(() =>
                OpenXrEyeRenderPipelineFactory.Create(false, EVrRenderPipeline.Default, ERvcPipelineMode.ForwardPlusOracle, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static RenderPipeline LoadChild(string child)
    {
        RenderPipelineScriptLoadResult loaded = Load(child);
        loaded.Problems.ShouldBeEmpty(string.Join(Environment.NewLine, loaded.Problems));
        return loaded.Pipeline.ShouldNotBeNull();
    }

    private static RenderPipelineScriptLoadResult Load(string child, string? baseText = null)
    {
        baseText ??= RenderPipelineScriptWriter.Write(CreateBase());
        return RenderPipelineScriptLoader.Load(
            child,
            ChildPath,
            path => string.Equals(path, BasePath, StringComparison.OrdinalIgnoreCase) ? baseText : null);
    }

    private static CustomRenderPipeline CreateBase()
    {
        CustomRenderPipeline pipeline = new()
        {
            AuthoredResources =
            [
                new AuthoredRenderResource
                {
                    Name = "Color",
                    Kind = EAuthoredRenderResourceKind.Texture,
                    Format = ESizedInternalFormat.Rgba8,
                },
                new AuthoredRenderResource
                {
                    Name = "ColorFBO",
                    Kind = EAuthoredRenderResourceKind.FrameBuffer,
                    Attachments = [new AuthoredAttachment { ResourceName = "Color" }],
                },
                new AuthoredRenderResource
                {
                    Name = "Unused",
                    Kind = EAuthoredRenderResourceKind.Texture,
                },
            ],
            AuthoredStages =
            [
                new AuthoredPostProcessStage
                {
                    Key = "my.glow",
                    DisplayName = "Glow",
                    Category = "effects",
                    Parameters = [new AuthoredPostProcessParameter { Name = "Strength", Kind = PostProcessParameterKind.Float, Min = 0f, Max = 4f, Default = [1f, 0f, 0f, 0f] }],
                },
            ],
            RenderPasses = new Dictionary<int, IComparer<XREngine.Rendering.Commands.RenderCommand>?> { [0] = null },
        };

        ViewportRenderCommandContainer commands = new(pipeline);
        commands.Add(new VPRC_Annotation { Label = "First", AnchorId = "base.first" });
        commands.Add(new VPRC_Annotation { Label = "Second", AnchorId = "base.second" });
        commands.Add(new VPRC_Annotation { Label = "Third", AnchorId = "base.third" });
        commands.Add(new VPRC_Annotation { Label = "Fourth", AnchorId = "base.fourth" });
        commands.Add(new VPRC_Annotation { Label = "Fifth", AnchorId = "base.fifth" });
        ViewportRenderCommandContainer bloomOn = new(pipeline);
        bloomOn.Add(new VPRC_Annotation { Label = "Bloom on" });
        commands.Add(new VPRC_IfElse
        {
            Label = "Bloom",
            AnchorId = "base.bloom",
            Condition = RenderBranchCondition.Variable("UseBloom", ERenderBranchConditionOperator.Equal, 1.0),
            TrueCommands = bloomOn,
        });
        pipeline.Commands = commands;
        return pipeline;
    }

    /// <summary>Gets the label of each top-level command: the annotation label, or the branch label.</summary>
    private static List<string> TopLevelLabels(ViewportRenderCommandContainer container)
    {
        List<string> labels = [];
        for (int i = 0; i < container.Count; i++)
        {
            string? label = container[i] switch
            {
                VPRC_Annotation annotation => annotation.Label,
                VPRC_IfElse branch => branch.Label,
                _ => null,
            };
            if (label is not null)
                labels.Add(label);
        }

        return labels;
    }

    private static void CollectAnchors(ViewportRenderCommandContainer container, List<string> anchors)
    {
        List<RenderCommandChildContainer> children = [];
        for (int i = 0; i < container.Count; i++)
        {
            ViewportRenderCommand command = container[i];
            if (command.AnchorId is { } id)
                anchors.Add(id);

            children.Clear();
            command.CollectChildContainers(children);
            for (int c = 0; c < children.Count; c++)
                CollectAnchors(children[c].Container, anchors);
        }
    }
}
