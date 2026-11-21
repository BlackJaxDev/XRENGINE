using NUnit.Framework;
using Shouldly;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Overlays;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class RenderPipelineOverlayEditsTests
{
    private static DefaultRenderPipeline LoadDerived(string text)
    {
        RenderPipelineScriptLoadResult loaded = RenderPipelineScriptLoader.Load(text);
        loaded.Problems.Where(static p => p.IsError).ShouldBeEmpty(string.Join(Environment.NewLine, loaded.Problems));
        return loaded.Pipeline.ShouldBeOfType<DefaultRenderPipeline>();
    }

    [Test]
    public void RemovePatch_RevertsOnePatch()
    {
        DefaultRenderPipeline pipeline = LoadDerived("derive Default;\nafter(post.bloom) {\n    annotation(Label=\"Kept\");\n}\nremove(post.dof);\n");
        RenderCommandPaths.TryFindAnchor(pipeline.CommandChain, "post.dof", out _, out _).ShouldBeFalse();
        RenderCommandPatch remove = pipeline.Overlays[0].CommandPatches[1];

        IReadOnlyList<RenderPipelineOverlay> reverted = RenderPipelineOverlayEdits.RemovePatch(pipeline.Overlays, 0, remove).ShouldNotBeNull();
        pipeline.Overlays = reverted;

        pipeline.Overlays[0].CommandPatches.Count.ShouldBe(1);
        RenderCommandPaths.TryFindAnchor(pipeline.CommandChain, "post.dof", out _, out _).ShouldBeTrue();
        RenderCommandPaths.TryFindAnchor(pipeline.CommandChain, "overlay0.patch0.cmd0", out _, out _).ShouldBeTrue();
    }

    [Test]
    public void AcceptRebase_TakesTheRenamedAnchorAndTheCurrentHash()
    {
        DefaultRenderPipeline pipeline = new();
        pipeline.AnchorAliases["old.bloom"] = "post.bloom";
        RenderPipelineOverlay overlay = new() { Name = "test", BaseTypeName = typeof(DefaultRenderPipeline).FullName!, BaseStructureHash = 1UL };
        overlay.CommandPatches.Add(new RenderCommandPatch { Kind = ERenderCommandPatchKind.Remove, Target = "old.bloom" });
        pipeline.Overlays = [overlay];
        pipeline.OverlayReport.CommandResults[0].Status.ShouldBe(ERenderPipelinePatchStatus.AnchorMoved);
        RenderPipelineOverlayEdits.NeedsRebase(pipeline, 0).ShouldBeTrue();

        pipeline.Overlays = RenderPipelineOverlayEdits.AcceptRebase(pipeline, 0).ShouldNotBeNull();

        pipeline.Overlays[0].CommandPatches[0].Target.ShouldBe("post.bloom");
        pipeline.OverlayReport.CommandResults[0].Status.ShouldBe(ERenderPipelinePatchStatus.Applied);
        RenderPipelineOverlayEdits.NeedsRebase(pipeline, 0).ShouldBeFalse();
    }
}
