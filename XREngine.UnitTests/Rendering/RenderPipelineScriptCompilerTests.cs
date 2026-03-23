using System.Numerics;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

public sealed class RenderPipelineScriptCompilerTests
{
    [Fact]
    public void CompileScript_ProducesExpectedCommandTree()
    {
        ScriptedTestPipeline pipeline = new();

        ViewportRenderCommandContainer root = pipeline.CommandChain;

        Assert.Equal(4, root.Count);

        VPRC_SetVariable setBool = Assert.IsType<VPRC_SetVariable>(root[0]);
        Assert.Equal("UseAo", setBool.VariableName);
        Assert.True(setBool.BoolValue);

        VPRC_IfElse ifElse = Assert.IsType<VPRC_IfElse>(root[1]);
        Assert.NotNull(ifElse.TrueCommands);
        Assert.NotNull(ifElse.FalseCommands);
        Assert.Equal(ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit, ifElse.TrueCommands!.BranchResources);
        Assert.Same(pipeline, ifElse.TrueCommands.ParentPipeline);
        Assert.Same(pipeline, ifElse.FalseCommands!.ParentPipeline);
        Assert.IsType<TestMarkerCommand>(ifElse.TrueCommands[0]);
        Assert.IsType<VPRC_SetVariable>(ifElse.FalseCommands[0]);

        VPRC_Switch switchCommand = Assert.IsType<VPRC_Switch>(root[2]);
        Assert.NotNull(switchCommand.Cases);
        Assert.Equal(2, switchCommand.Cases!.Count);
        Assert.Equal(ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit, switchCommand.Cases[2].BranchResources);
        Assert.IsType<TestMarkerCommand>(switchCommand.Cases[1][0]);
        Assert.IsType<TestMarkerCommand>(switchCommand.DefaultCase![0]);

        VPRC_SetVariable clear = Assert.IsType<VPRC_SetVariable>(root[3]);
        Assert.Equal("Transient", clear.VariableName);
        Assert.True(clear.ClearVariable);
    }

    [Fact]
    public void CompileScript_CreatesFreshCommandsForGenericFactories()
    {
        RenderPipelineScript script = RenderPipelineScript.Create(builder =>
            builder.Command<TestMarkerCommand>(cmd => cmd.Name = "Compiled"));

        ScriptedTestPipeline firstPipeline = new(script);
        ScriptedTestPipeline secondPipeline = new(script);

        TestMarkerCommand first = Assert.IsType<TestMarkerCommand>(firstPipeline.CommandChain[0]);
        TestMarkerCommand second = Assert.IsType<TestMarkerCommand>(secondPipeline.CommandChain[0]);

        Assert.NotSame(first, second);
        Assert.Equal("Compiled", first.Name);
        Assert.Equal("Compiled", second.Name);
    }

    [Fact]
    public void ParseScript_CompilesCommandsBranchesAndProperties()
    {
        const string scriptText = """
            set UseAo = true;
            clear(Color=true, Depth=true);
            debugOverlay(
                SourceTextureName=HdrScene,
                ChannelMode=RGBA,
                NormalizedRegion=vec4(0.1, 0.2, 0.3, 0.4),
                Multiply=vec4(1.0, 0.5, 0.25, 1.0)
            );
            if (UseAo) {
                depthWrite(Allow=false);
            } else {
                clear UseAo;
            }
            switch (2) {
                case 1: {
                    annotation(Label="One");
                }
                case 2: {
                    annotation(Label="Two");
                }
                default: {
                    annotation(Label="Fallback");
                }
            }
            """;

        RenderPipelineScript script = RenderPipelineScript.Parse(scriptText);
        ScriptedTestPipeline pipeline = new(script);
        ViewportRenderCommandContainer root = pipeline.CommandChain;

        Assert.Equal(5, root.Count);

        VPRC_SetVariable setUseAo = Assert.IsType<VPRC_SetVariable>(root[0]);
        Assert.True(setUseAo.BoolValue);

        VPRC_Clear clear = Assert.IsType<VPRC_Clear>(root[1]);
        Assert.True(clear.Color);
        Assert.True(clear.Depth);

        VPRC_DebugOverlay overlay = Assert.IsType<VPRC_DebugOverlay>(root[2]);
        Assert.Equal("HdrScene", overlay.SourceTextureName);
        Assert.Equal(EDebugOverlayChannelMode.RGBA, overlay.ChannelMode);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), overlay.NormalizedRegion);
        Assert.Equal(new Vector4(1.0f, 0.5f, 0.25f, 1.0f), overlay.Multiply);

        VPRC_IfElse ifElse = Assert.IsType<VPRC_IfElse>(root[3]);
        Assert.IsType<VPRC_DepthWrite>(ifElse.TrueCommands![0]);
        Assert.IsType<VPRC_SetVariable>(ifElse.FalseCommands![0]);

        VPRC_Switch switchCommand = Assert.IsType<VPRC_Switch>(root[4]);
        Assert.Equal("Two", Assert.IsType<VPRC_Annotation>(switchCommand.Cases![2][0]).Label);
        Assert.Equal("Fallback", Assert.IsType<VPRC_Annotation>(switchCommand.DefaultCase![0]).Label);
    }

    [Fact]
    public void ParseScript_ExposesCurrentCommandTypes()
    {
        Assert.Contains("clear", RenderPipelineScript.CommandTypes.Keys);
        Assert.Contains("debugOverlay", RenderPipelineScript.CommandTypes.Keys);
        Assert.Contains("setClears", RenderPipelineScript.CommandTypes.Keys);
        Assert.Contains(nameof(VPRC_Clear), RenderPipelineScript.CommandTypes.Keys);
        Assert.Contains(nameof(VPRC_DebugOverlay), RenderPipelineScript.CommandTypes.Keys);
        Assert.DoesNotContain("VPRC_ForEach", RenderPipelineScript.CommandTypes.Keys);
    }

    [Fact]
    public void ParseScript_AcceptsLegacyVprcCommandAliases()
    {
        const string scriptText = """
            VPRC_Clear(Color=true, Depth=true);
            VPRC_DebugOverlay(SourceTextureName="HdrScene");
            """;

        ScriptedTestPipeline pipeline = new(RenderPipelineScript.Parse(scriptText));

        VPRC_Clear clear = Assert.IsType<VPRC_Clear>(pipeline.CommandChain[0]);
        Assert.True(clear.Color);
        Assert.True(clear.Depth);
        Assert.IsType<VPRC_DebugOverlay>(pipeline.CommandChain[1]);
    }

    [Fact]
    public void ParseScript_SupportsRepeatConditionalAndCascadeFlowStatements()
    {
        const string scriptText = """
            repeat(3) {
                annotation(Label="Loop");
            }
            when(VariableName=ShowOverlay, Comparison=Truthy, Invert=false) {
                debugOverlay(SourceTextureName="DepthView");
            }
            foreach_cascade(DirectionalLightIndex=0, BindCascadeFrameBuffer=true, ClearDepth=true) {
                annotation(Label="Cascade");
            }
            """;

        ScriptedTestPipeline pipeline = new(RenderPipelineScript.Parse(scriptText));
        ViewportRenderCommandContainer root = pipeline.CommandChain;

        VPRC_Repeat repeat = Assert.IsType<VPRC_Repeat>(root[0]);
        Assert.Equal(3, repeat.Count);
        Assert.NotNull(repeat.Body);
        Assert.IsType<VPRC_Annotation>(repeat.Body![0]);

        VPRC_ConditionalRender conditionalRender = Assert.IsType<VPRC_ConditionalRender>(root[1]);
        Assert.Equal("ShowOverlay", conditionalRender.VariableName);
        Assert.Equal(EConditionalRenderComparison.Truthy, conditionalRender.Comparison);
        Assert.NotNull(conditionalRender.Body);
        Assert.IsType<VPRC_DebugOverlay>(conditionalRender.Body![0]);

        VPRC_ForEachCascade forEachCascade = Assert.IsType<VPRC_ForEachCascade>(root[2]);
        Assert.Equal(0, forEachCascade.DirectionalLightIndex);
        Assert.True(forEachCascade.BindCascadeFrameBuffer);
        Assert.True(forEachCascade.ClearDepth);
        Assert.NotNull(forEachCascade.Body);
        Assert.IsType<VPRC_Annotation>(forEachCascade.Body![0]);
    }

    [Fact]
    public void ExportScript_PreservesSupportedFlowStatements()
    {
        ScriptedTestPipeline pipeline = new(RenderPipelineScript.Parse("""
            repeat(2) {
                annotation(Label="Loop");
            }
            when(VariableName=Visible, Comparison=Truthy) {
                annotation(Label="Conditional");
            }
            foreach_cascade(DirectionalLightIndex=1) {
                annotation(Label="Cascade");
            }
            """));

        string script = RenderPipelineScript.Export(pipeline.CommandChain);

        Assert.Contains("repeat(2)", script);
        Assert.Contains("when(", script);
        Assert.Contains("foreach_cascade(", script);
    }

    [Fact]
    public void ParseScript_SupportsIndexedExpressionsForRuntimeVariables()
    {
        ScriptedTestPipeline pipeline = new(RenderPipelineScript.Parse("""
            set SelectedCascade = CascadeIndices[1];
            """));

        VPRC_SetVariable setVariable = Assert.IsType<VPRC_SetVariable>(pipeline.CommandChain[0]);
        Assert.Null(setVariable.IntValue);
        Assert.NotNull(setVariable.ValueEvaluator);
    }

    [Fact]
    public void ExampleXrsScript_ParsesSuccessfully()
    {
        string scriptPath = Path.Combine(GetRepositoryRoot(), "docs", "features", "render-pipeline-script-example.xrs");
        string scriptText = File.ReadAllText(scriptPath);

        ScriptedTestPipeline pipeline = new(RenderPipelineScript.Parse(scriptText));

        Assert.NotEmpty(pipeline.CommandChain);
        Assert.IsType<VPRC_Clear>(pipeline.CommandChain[2]);
        Assert.IsType<VPRC_IfElse>(pipeline.CommandChain[4]);
        Assert.IsType<VPRC_Switch>(pipeline.CommandChain[5]);
        Assert.IsType<VPRC_Repeat>(pipeline.CommandChain[6]);
        Assert.IsType<VPRC_ForEachCascade>(pipeline.CommandChain[7]);
    }

    [Fact]
    public void DefaultRenderPipeline_ExportsSampleScriptThatRoundTripsCommandShape()
    {
        DefaultRenderPipeline pipeline = new(stereo: false);
        string sampleScript = RenderPipelineScript.Export(pipeline.CommandChain);

        Assert.False(string.IsNullOrWhiteSpace(sampleScript));
        Assert.Contains("if (false)", sampleScript);
        Assert.Contains("setClears", sampleScript);
        Assert.DoesNotContain(nameof(VPRC_SetClears), sampleScript);

        ScriptedTestPipeline scriptedPipeline = new(RenderPipelineScript.Parse(sampleScript));
        string roundTrippedScript = RenderPipelineScript.Export(scriptedPipeline.CommandChain);

        Assert.Equal(sampleScript, roundTrippedScript);
        AssertCommandShapeEqual(pipeline.CommandChain, scriptedPipeline.CommandChain);
    }

    private sealed class ScriptedTestPipeline : RenderPipeline
    {
        private readonly RenderPipelineScript? _script;

        public ScriptedTestPipeline(RenderPipelineScript? script = null)
            : base(true)
        {
            _script = script;
            CommandChain = GenerateCommandChain();
            PassIndicesAndSorters = GetPassIndicesAndSorters();
        }

        protected override Lazy<XRMaterial> InvalidMaterialFactory
            => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain()
        {
            if (_script is not null)
                return _script.Compile(this);

            return CompileScript(builder =>
            {
                builder.SetVariable("UseAo", true);
                builder.If(static () => true, branch =>
                {
                    branch.Then(then => then.Command<TestMarkerCommand>(cmd => cmd.Name = "IfTrue"),
                        ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit);
                    branch.Else(@else => @else.SetVariable("AoMode", 0));
                });
                builder.Switch(static () => 2, switchBuilder =>
                {
                    switchBuilder.Case(1, branch => branch.Command<TestMarkerCommand>(cmd => cmd.Name = "Case1"));
                    switchBuilder.Case(2, branch => branch.Command<TestMarkerCommand>(cmd => cmd.Name = "Case2"),
                        ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit);
                    switchBuilder.Default(branch => branch.Command<TestMarkerCommand>(cmd => cmd.Name = "Default"));
                });
                builder.ClearVariable("Transient");
            });
        }

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => [];
    }

    private sealed class TestMarkerCommand : ViewportRenderCommand
    {
        public string Name { get; set; } = string.Empty;
        public Vector4 Marker { get; set; }

        protected override void Execute()
        {
        }
    }

    private static void AssertCommandShapeEqual(ViewportRenderCommandContainer expected, ViewportRenderCommandContainer actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            ViewportRenderCommand expectedCommand = expected[i];
            ViewportRenderCommand actualCommand = actual[i];

            Assert.Equal(expectedCommand.GetType(), actualCommand.GetType());

            switch (expectedCommand)
            {
                case VPRC_IfElse expectedIfElse:
                    VPRC_IfElse actualIfElse = Assert.IsType<VPRC_IfElse>(actualCommand);
                    Assert.NotNull(expectedIfElse.TrueCommands);
                    Assert.NotNull(actualIfElse.TrueCommands);
                    AssertCommandShapeEqual(expectedIfElse.TrueCommands!, actualIfElse.TrueCommands!);

                    if (expectedIfElse.FalseCommands is null)
                    {
                        Assert.Null(actualIfElse.FalseCommands);
                    }
                    else
                    {
                        Assert.NotNull(actualIfElse.FalseCommands);
                        AssertCommandShapeEqual(expectedIfElse.FalseCommands, actualIfElse.FalseCommands!);
                    }
                    break;

                case VPRC_Switch expectedSwitch:
                    VPRC_Switch actualSwitch = Assert.IsType<VPRC_Switch>(actualCommand);
                    Assert.Equal(expectedSwitch.Cases?.Keys.OrderBy(static x => x), actualSwitch.Cases?.Keys.OrderBy(static x => x));

                    if (expectedSwitch.Cases is not null)
                    {
                        Assert.NotNull(actualSwitch.Cases);
                        foreach ((int key, ViewportRenderCommandContainer expectedCase) in expectedSwitch.Cases)
                        {
                            Assert.True(actualSwitch.Cases!.TryGetValue(key, out ViewportRenderCommandContainer? actualCase));
                            Assert.NotNull(actualCase);
                            AssertCommandShapeEqual(expectedCase, actualCase!);
                        }
                    }

                    if (expectedSwitch.DefaultCase is null)
                    {
                        Assert.Null(actualSwitch.DefaultCase);
                    }
                    else
                    {
                        Assert.NotNull(actualSwitch.DefaultCase);
                        AssertCommandShapeEqual(expectedSwitch.DefaultCase, actualSwitch.DefaultCase!);
                    }
                    break;

                case VPRC_Repeat expectedRepeat:
                    VPRC_Repeat actualRepeat = Assert.IsType<VPRC_Repeat>(actualCommand);
                    Assert.Equal(expectedRepeat.Count, actualRepeat.Count);
                    Assert.NotNull(expectedRepeat.Body);
                    Assert.NotNull(actualRepeat.Body);
                    AssertCommandShapeEqual(expectedRepeat.Body!, actualRepeat.Body!);
                    break;

                case VPRC_ConditionalRender expectedConditionalRender:
                    VPRC_ConditionalRender actualConditionalRender = Assert.IsType<VPRC_ConditionalRender>(actualCommand);
                    Assert.Equal(expectedConditionalRender.VariableName, actualConditionalRender.VariableName);
                    Assert.Equal(expectedConditionalRender.Comparison, actualConditionalRender.Comparison);
                    Assert.Equal(expectedConditionalRender.Invert, actualConditionalRender.Invert);
                    Assert.NotNull(expectedConditionalRender.Body);
                    Assert.NotNull(actualConditionalRender.Body);
                    AssertCommandShapeEqual(expectedConditionalRender.Body!, actualConditionalRender.Body!);
                    break;

                case VPRC_ForEachCascade expectedForEachCascade:
                    VPRC_ForEachCascade actualForEachCascade = Assert.IsType<VPRC_ForEachCascade>(actualCommand);
                    Assert.Equal(expectedForEachCascade.DirectionalLightName, actualForEachCascade.DirectionalLightName);
                    Assert.Equal(expectedForEachCascade.DirectionalLightIndex, actualForEachCascade.DirectionalLightIndex);
                    Assert.Equal(expectedForEachCascade.BindCascadeFrameBuffer, actualForEachCascade.BindCascadeFrameBuffer);
                    Assert.NotNull(expectedForEachCascade.Body);
                    Assert.NotNull(actualForEachCascade.Body);
                    AssertCommandShapeEqual(expectedForEachCascade.Body!, actualForEachCascade.Body!);
                    break;
            }
        }
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find repository root from '{AppContext.BaseDirectory}'.");
    }
}
