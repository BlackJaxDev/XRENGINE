using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The post-process state of a camera that a viewport renders with a kept pipeline during a camera claim.
/// </summary>
[TestFixture]
public sealed class CameraClaimPostProcessStateTests
{
    private static ColorGradingSettings Grading(PipelinePostProcessState state)
    {
        state.GetStage<ColorGradingSettings>()!.TryGetBacking(out ColorGradingSettings? grading).ShouldBeTrue();
        return grading!;
    }

    [Test]
    public void BindToAnotherPipelineOfTheSameType_KeepsTheBackingObject()
    {
        DefaultRenderPipeline first = new(stereo: false);
        DefaultRenderPipeline second = new(stereo: false);
        PipelinePostProcessState state = new();
        state.BindToPipeline(first);
        ColorGradingSettings grading = Grading(state);
        grading.ResetAutoExposureHistory();

        state.BindToPipeline(second);

        Grading(state).ShouldBeSameAs(grading);
        grading.IsAutoExposureResetPending.ShouldBeTrue();
    }

    [Test]
    public void QueryWithNoPipeline_DuringAClaim_UsesTheKeptPipeline()
    {
        DefaultRenderPipeline kept = new(stereo: false);
        XRCamera camera = new();
        camera.NoteClaimBorrowedPipeline(kept);

        PipelinePostProcessState state = camera.GetPostProcessState(null)!;

        state.PipelineId.ShouldBe(kept.ID);
        camera.PostProcessStates.DefaultState.ShouldBeSameAs(state);
        camera.GetPostProcessState(null).ShouldBeSameAs(state);
        state.PipelineId.ShouldBe(kept.ID);
    }

    [Test]
    public void ResetAutoExposureHistory_OnACameraWithNoState_ResetsTheStateThatTheClaimUses()
    {
        DefaultRenderPipeline kept = new(stereo: false);
        XRCamera camera = new();
        camera.NoteClaimBorrowedPipeline(kept);

        camera.ResetAutoExposureHistory();

        Grading(camera.GetPostProcessState(kept)!).IsAutoExposureResetPending.ShouldBeTrue();
    }
}
