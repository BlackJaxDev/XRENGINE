using NUnit.Framework;
using Shouldly;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class RenderPipelineTemplateTests
{
    private static string TemplateDirectory
    {
        get
        {
            string current = Path.GetFullPath(AppContext.BaseDirectory);
            while (true)
            {
                string candidate = Path.Combine(current, "Build", "CommonAssets", RenderPipelineTemplates.RelativeDirectory);
                if (Directory.Exists(candidate))
                    return candidate;

                string? parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent))
                    throw new DirectoryNotFoundException("Unable to locate the render pipeline template folder.");
                current = parent;
            }
        }
    }

    private static IEnumerable<RenderPipelineTemplate> Templates => RenderPipelineTemplates.All;

    private static List<RenderPipelineProblem> Validate(CustomRenderPipeline pipeline)
    {
        RenderPipelineResourceProfile profile = RenderPipelineValidationContext.DefaultProfile;
        RenderPipelineDraftState state = RenderPipelineDraftState.From(pipeline);
        RenderPipelineDraftPreview preview = RenderPipelineDraftPreview.Build(state, 0, profile);
        List<RenderPipelineProblem> problems = [];
        problems.AddRange(preview.Validate(pipeline, RendererBackendId.OpenGL, profile).Problems);
        problems.AddRange(preview.Validate(pipeline, RendererBackendId.Vulkan, profile).Problems);
        return problems;
    }

    [TestCaseSource(nameof(Templates))]
    public void Template_LoadsAndValidatesWithoutErrors(RenderPipelineTemplate template)
    {
        RenderPipelineScriptLoadResult result = RenderPipelineTemplates.Load(template, TemplateDirectory);

        result.Problems.ShouldBeEmpty();
        CustomRenderPipeline pipeline = result.Pipeline.ShouldBeOfType<CustomRenderPipeline>();
        Validate(pipeline).Where(p => p.Severity != ERenderPipelineProblemSeverity.Info).ShouldBeEmpty();
    }

    [TestCaseSource(nameof(Templates))]
    public void Template_WritesTheSameTextTwice(RenderPipelineTemplate template)
    {
        RenderPipeline first = RenderPipelineTemplates.Load(template, TemplateDirectory).Pipeline.ShouldNotBeNull();
        string text = RenderPipelineScriptWriter.Write(first);

        RenderPipeline second = RenderPipelineScriptLoader.Load(text).Pipeline.ShouldNotBeNull();
        RenderPipelineScriptWriter.Write(second).ShouldBe(text);
    }

    [Test]
    public void Wizard_MinimalForwardWithoutBloom_DropsTheBranchAndItsResources()
    {
        NewRenderPipelineWizard wizard = new()
        {
            Source = ENewRenderPipelineSource.Template,
            Template = RenderPipelineTemplates.MinimalForward,
            TemplateDirectory = TemplateDirectory,
        };
        wizard.Build().ShouldBeTrue();
        wizard.AvailableStages.ShouldContain("bloom");
        wizard.Text.ShouldContain("bloomPass");

        wizard.RemovedStages.Add("bloom");
        wizard.Build().ShouldBeTrue();

        wizard.Problems.ShouldBeEmpty();
        wizard.ValidationProblems.ShouldBeEmpty();
        wizard.Text.ShouldNotContain("bloom");
        wizard.Text.ShouldNotContain("BloomOutputTexture");
        wizard.Text.ShouldContain("tonemap(");
    }

    [Test]
    public void Wizard_DeriveFromBase_WritesTheDeriveHeader()
    {
        RenderPipelineScriptBases.TryGet("Advanced", out RenderPipelineScriptBase? entry).ShouldBeTrue();
        NewRenderPipelineWizard wizard = new() { Source = ENewRenderPipelineSource.DeriveFromBase };
        wizard.SelectBase(entry);
        wizard.SetBaseArgument(0, true);

        wizard.Build().ShouldBeTrue();

        wizard.Text.ShouldContain("derive Advanced(stereo: true);");
        RenderPipelineScriptLoader.Load(wizard.Text).Pipeline.ShouldBeOfType<AdvancedRenderPipeline>();
    }

    [Test]
    public void Wizard_DeriveFromScript_WritesARelativeParentPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), string.Concat("xrs-wizard-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            string parent = Path.Combine(directory, "parent.xrs");
            File.WriteAllText(parent, RenderPipelineScriptWriter.Write(new DefaultRenderPipeline()));
            NewRenderPipelineWizard wizard = new()
            {
                Source = ENewRenderPipelineSource.DeriveFromScript,
                ParentScriptPath = parent,
                OutputPath = Path.Combine(directory, "child", "child.xrs"),
            };

            wizard.Build().ShouldBeTrue();
            wizard.Text.ShouldContain("derive \"../parent.xrs\";");

            string created = wizard.Create();
            RenderPipelineScriptLoader.LoadFile(created).Pipeline.ShouldBeOfType<DefaultRenderPipeline>();
            wizard.CanCreate.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ScriptEditor_WithASyntaxError_LeavesTheDraftUnchanged()
    {
        CustomRenderPipeline pipeline = RenderPipelineTemplates.Load(RenderPipelineTemplates.MinimalForward, TemplateDirectory).Pipeline
            .ShouldBeOfType<CustomRenderPipeline>();
        RenderPipelineDraft draft = new(pipeline, static action => action());
        RenderPipelineScriptDraftEditor editor = new();
        editor.Begin(draft);
        int revision = draft.Revision;
        int commandCount = draft.State.Commands.Count;

        editor.Text = string.Concat(editor.Text, "\nrenderMeshesPass(RenderPass=");
        editor.Apply().ShouldBeFalse();

        editor.Problems.ShouldNotBeEmpty();
        editor.Problems[0].Line.ShouldBeGreaterThan(0);
        draft.Revision.ShouldBe(revision);
        draft.State.Commands.Count.ShouldBe(commandCount);
    }

    [Test]
    public void ScriptEditor_AppliesValidTextAsOneUndoStep()
    {
        CustomRenderPipeline pipeline = RenderPipelineTemplates.Load(RenderPipelineTemplates.Blank, TemplateDirectory).Pipeline
            .ShouldBeOfType<CustomRenderPipeline>();
        RenderPipelineDraft draft = new(pipeline, static action => action());
        RenderPipelineScriptDraftEditor editor = new();
        editor.Begin(draft);

        editor.Text = string.Concat(editor.Text, "\npasses {\n    scene 0;\n}\n\nrenderMeshesPass();\n");
        editor.Apply().ShouldBeTrue();

        editor.Problems.ShouldBeEmpty();
        draft.State.Commands.Count.ShouldBe(1);
        draft.State.RenderPasses.ShouldContainKey(0);
        editor.IsDraftNewer.ShouldBeFalse();
    }

    [Test]
    public void ScriptEditor_RejectsADerivedScript()
    {
        RenderPipelineScriptDraftEditor.TryLoadState("derive Default;\n", out RenderPipelineDraftState? state, out IReadOnlyList<RenderPipelineScriptProblem> problems)
            .ShouldBeFalse();

        state.ShouldBeNull();
        problems.ShouldContain(p => p.IsError);
    }
}
