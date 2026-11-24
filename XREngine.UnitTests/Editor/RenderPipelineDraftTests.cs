using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class RenderPipelineDraftTests
{
    private static RenderPipelineDraft CreateDraft(CustomRenderPipeline pipeline)
        => new(pipeline, static action => action());

    private static CustomRenderPipeline CreatePipeline()
    {
        ViewportRenderCommandContainer commands = new();
        commands.Add(new VPRC_Annotation());
        return new CustomRenderPipeline { Commands = commands };
    }

    [Test]
    public void DraftEdits_DoNotChangeTheLivePipeline()
    {
        CustomRenderPipeline pipeline = CreatePipeline();
        RenderPipelineDraft draft = CreateDraft(pipeline);

        draft.Edit("Add", state => state.Commands.Add(new VPRC_Annotation())).ShouldBeTrue();
        draft.Edit("Add resource", state => RenderPipelineDraftResourceEdits.Add(state, EAuthoredRenderResourceKind.Texture)).ShouldBeTrue();

        pipeline.Commands!.Count.ShouldBe(1);
        pipeline.AuthoredResources.ShouldBeEmpty();
        draft.State.Commands.Count.ShouldBe(2);
        draft.IsDirty.ShouldBeTrue();
        draft.State.Commands[0].ShouldNotBeSameAs(pipeline.Commands[0]);
    }

    [Test]
    public void Apply_WritesACopy_AndKeepsTheDraftOpen()
    {
        CustomRenderPipeline pipeline = CreatePipeline();
        RenderPipelineDraft draft = CreateDraft(pipeline);
        draft.Edit("Add", state => state.Commands.Add(new VPRC_Annotation()));

        draft.Apply().ShouldBeTrue();

        draft.IsDirty.ShouldBeFalse();
        draft.ApplyFailure.ShouldBeNull();
        pipeline.Commands!.Count.ShouldBe(2);
        pipeline.Commands[1].ShouldNotBeSameAs(draft.State.Commands[1]);
        draft.IsClosed.ShouldBeFalse();
    }

    [Test]
    public void ReportApplyFailure_KeepsTheDraftDirty()
    {
        RenderPipelineDraft draft = CreateDraft(CreatePipeline());
        draft.Apply();
        draft.ReportApplyFailure("The pending generation did not commit.");

        draft.IsDirty.ShouldBeTrue();
        draft.ApplyFailure.ShouldNotBeNull();
    }

    [Test]
    public void UndoAndRedo_RestoreEachEdit()
    {
        Undo.ClearHistory();
        try
        {
            RenderPipelineDraft draft = CreateDraft(CreatePipeline());
            draft.Edit("Add", state => state.Commands.Add(new VPRC_Annotation()));
            ViewportRenderCommand added = draft.State.Commands[1];
            draft.Edit("Add resource", state => RenderPipelineDraftResourceEdits.Add(state, EAuthoredRenderResourceKind.Buffer));

            Undo.TryUndo().ShouldBeTrue();
            draft.State.Resources.ShouldBeEmpty();
            draft.State.Commands.Count.ShouldBe(2);

            Undo.TryUndo().ShouldBeTrue();
            draft.State.Commands.Count.ShouldBe(1);

            Undo.TryRedo().ShouldBeTrue();
            draft.State.Commands.Count.ShouldBe(2);
            Undo.TryRedo().ShouldBeTrue();
            draft.State.Resources.Count.ShouldBe(1);

            // Redo restores the same objects, so a selection of the added command stays valid.
            draft.State.Commands[1].ShouldBeSameAs(added);
        }
        finally
        {
            Undo.ClearHistory();
        }
    }

    [Test]
    public void Validation_DropsStaleRevisions()
    {
        RenderPipelineDraft draft = CreateDraft(CreatePipeline());
        RenderPipelineDraftValidation validation = new(static work => Task.FromResult(work()));

        validation.Request(draft, RenderPipelineValidationContext.DefaultProfile, RendererBackendId.OpenGL).ShouldNotBeNull()!.Wait();
        validation.Request(draft, RenderPipelineValidationContext.DefaultProfile, RendererBackendId.OpenGL).ShouldBeNull();
        validation.TryGetCurrent(draft, out _).ShouldBeTrue();

        draft.Edit("Add", state => state.Commands.Add(new VPRC_Annotation()));
        validation.TryGetCurrent(draft, out _).ShouldBeFalse();
        validation.Request(draft, RenderPipelineValidationContext.DefaultProfile, RendererBackendId.OpenGL)!.Wait();
        validation.TryGetCurrent(draft, out RenderPipelineDraftValidation.Result result).ShouldBeTrue();
        result.Revision.ShouldBe(draft.Revision);
        validation.RequestCount.ShouldBe(2);
    }

    [Test]
    public void Cloner_MapsPopCommandsToTheCopiedPush()
    {
        ViewportRenderCommandContainer source = new();
        VPRC_BindFBOByName bind = new() { FrameBufferName = "Target" };
        source.Add(bind);
        source.Add(new VPRC_Annotation());
        source.Add(bind.PopCommand);

        ViewportRenderCommandContainer copy = RenderCommandGraphCloner.CloneContainer(source);

        copy.Count.ShouldBe(3);
        VPRC_BindFBOByName copiedBind = copy[0].ShouldBeOfType<VPRC_BindFBOByName>();
        copiedBind.ShouldNotBeSameAs(bind);
        copiedBind.FrameBufferName.ShouldBe("Target");
        copy[2].ShouldBeSameAs(copiedBind.PopCommand);
    }

    [Test]
    public void FlowEdits_KeepStateScopesTogether()
    {
        RenderPipelineDraftState state = new();
        VPRC_BindFBOByName bind = new() { FrameBufferName = "Target" };
        VPRC_Annotation inner = new();
        VPRC_Annotation after = new();
        state.Commands.Add(bind);
        state.Commands.Add(inner);
        state.Commands.Add(bind.PopCommand);
        state.Commands.Add(after);

        RenderPipelineDraftEdits.MoveBy(state, after, -1).ShouldBeTrue();
        state.Commands[0].ShouldBeSameAs(after);

        ViewportRenderCommand? copy = RenderPipelineDraftEdits.Duplicate(state, bind);
        copy.ShouldNotBeNull();
        state.Commands.Count.ShouldBe(7);

        RenderPipelineDraftEdits.Delete(state, bind).ShouldBeTrue();
        state.Commands.IndexOf(inner).ShouldBe(1);
        state.Commands.Count.ShouldBe(5);

        VPRC_IfElse? branch = RenderPipelineDraftEdits.WrapInIf(state, inner);
        branch.ShouldNotBeNull();
        branch!.TrueCommands![0].ShouldBeSameAs(inner);
        branch.Condition.ShouldNotBeNull();

        VPRC_IfElse? extracted = RenderPipelineDraftEdits.ExtractToBranch(state, after);
        extracted.ShouldNotBeNull();
        extracted!.TrueCommands![0].ShouldBeSameAs(after);
        extracted.FalseCommands![0].ShouldNotBeSameAs(after);

        ViewportStateRenderCommandBase? scope = RenderPipelineDraftEdits.WrapInScope(state, extracted, typeof(VPRC_PushRenderArea));
        scope.ShouldNotBeNull();
        int scopeIndex = state.Commands.IndexOf(scope!);
        state.Commands[scopeIndex + 1].ShouldBeSameAs(extracted);
        state.Commands[scopeIndex + 2].ShouldBeSameAs(scope!.PairedPopCommand);
    }

    [Test]
    public void ResourceRename_UpdatesEveryReference()
    {
        RenderPipelineDraftState state = new();
        RenderPipelineSnippets.EnsureHdrTarget(state);
        VPRC_Tonemap tonemap = new() { SourceTextureName = RenderPipelineSnippets.HdrColorTexture };
        state.Commands.Add(tonemap);

        int changed = RenderPipelineDraftResourceEdits.Rename(state, RenderPipelineSnippets.HdrColorTexture, "SceneColor");

        changed.ShouldBe(2);
        tonemap.SourceTextureName.ShouldBe("SceneColor");
        RenderPipelineDraftResourceEdits.FindResource(state, RenderPipelineSnippets.HdrFrameBuffer)!.Attachments[0].ResourceName.ShouldBe("SceneColor");
        RenderPipelineDraftResourceEdits.Rename(state, "SceneColor", RenderPipelineSnippets.HdrDepthTexture).ShouldBe(-1);
    }

    [Test]
    public void ResourceDelete_IsBlockedWhileReferenced()
    {
        RenderPipelineDraftState state = new();
        RenderPipelineSnippets.EnsureHdrTarget(state);
        List<string> references = [];

        RenderPipelineDraftResourceEdits.TryDelete(state, RenderPipelineSnippets.HdrColorTexture, references).ShouldBeFalse();
        references.ShouldNotBeEmpty();

        RenderPipelineDraftResourceEdits.TryDelete(state, RenderPipelineSnippets.HdrFrameBuffer, references).ShouldBeTrue();
        RenderPipelineDraftResourceEdits.TryDelete(state, RenderPipelineSnippets.HdrColorTexture, references).ShouldBeTrue();
    }

    [Test]
    public void PassEdits_ChangeTheDraftMetadata()
    {
        RenderPipelineDraftState state = new();
        int first = RenderPipelineDraftPassEdits.AddScenePass(state);
        int second = RenderPipelineDraftPassEdits.AddScenePass(state);

        RenderPipelineDraftPassEdits.AddDependency(state, second, first).ShouldBeTrue();
        RenderPipelineDraftPassEdits.AddDependency(state, second, first).ShouldBeFalse();
        RenderPipelineDraftPassEdits.ChangeIndex(state, first, 10).ShouldBeTrue();
        state.RenderPasses.ContainsKey(10).ShouldBeTrue();
        RenderPipelineDraftPassEdits.RemoveScenePass(state, second).ShouldBeTrue();
        state.PassDependencies.ShouldBeEmpty();
    }

    [TestCaseSource(nameof(SnippetNames))]
    public void Snippets_ValidateWithoutErrors_InABlankPipeline(string snippetName)
    {
        RenderPipelineSnippet snippet = RenderPipelineSnippets.All.Single(s => s.Name == snippetName);
        RenderPipelineDraftState state = new();
        RenderPipelineSnippets.EnsureHdrTarget(state);
        RenderPipelineDraftEdits.InsertAfter(state, null, snippet.Build(state)).ShouldBeTrue();

        RenderPipelineDraftPreview preview = RenderPipelineDraftPreview.Build(state, 1, RenderPipelineValidationContext.DefaultProfile);
        RenderPipelineValidationReport report = preview.Validate(new CustomRenderPipeline(), RendererBackendId.OpenGL, RenderPipelineValidationContext.DefaultProfile);

        report.Problems.Where(p => p.Severity == ERenderPipelineProblemSeverity.Error).Select(p => p.Message).ShouldBeEmpty();
    }

    private static IEnumerable<string> SnippetNames()
        => RenderPipelineSnippets.All.Select(s => s.Name);

    [Test]
    public void Fork_DropsCodeConditions_AndBlocksApplyUntilFixed()
    {
        ViewportRenderCommandContainer commands = new();
        VPRC_IfElse branch = new()
        {
            Label = "Code branch",
            ConditionEvaluator = static () => true,
            TrueCommands = new ViewportRenderCommandContainer(),
        };
        commands.Add(branch);
        CustomRenderPipeline source = new() { Commands = commands };

        RenderPipelineForkResult fork = RenderPipelineFork.Create(source, RenderPipelineValidationContext.DefaultProfile);

        fork.Unconverted.ShouldContain(line => line.Contains(nameof(VPRC_IfElse.ConditionEvaluator), StringComparison.Ordinal));
        branch.ConditionEvaluator.ShouldNotBeNull();
        RenderPipelineDraft draft = CreateDraft(fork.Pipeline);
        RenderPipelineDraftPreview preview = RenderPipelineDraftPreview.Build(draft.State, draft.Revision, RenderPipelineValidationContext.DefaultProfile);
        RenderPipelineValidationReport report = preview.Validate(fork.Pipeline, RendererBackendId.OpenGL, RenderPipelineValidationContext.DefaultProfile);
        report.Has("flow.branch-without-condition").ShouldBeTrue();
        report.ErrorCount.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Palette_HidesCodeCommandsForDataPipelines()
    {
        ViewportRenderCommandContainer.RegisterCommandFactory<VPRC_Manual>();
        RenderPipelineCommandPalette palette = new() { Search = "Manual" };

        palette.Refresh(hideCodeCommands: true);
        palette.Items.ShouldNotContain(item => item.Command != null && item.Command.Type == typeof(VPRC_Manual));

        palette.Refresh(hideCodeCommands: false);
        palette.Items.ShouldContain(item => item.Command != null && item.Command.Type == typeof(VPRC_Manual));
    }
}
