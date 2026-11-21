using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The post-process state of a camera that a viewport renders with a kept pipeline during a camera claim.
/// The render-context resolver seam acts as the render thread: it gives the pipeline that renders the camera, and null
/// stands for a query from the update thread.
/// </summary>
[TestFixture]
public sealed class CameraClaimPostProcessStateTests
{
    private Func<XRCamera, RenderPipeline?> _originalResolver = null!;
    private XRCamera? _renderedCamera;
    private RenderPipeline? _renderingPipeline;
    private bool _onRenderThread;

    [SetUp]
    public void SetUp()
    {
        _originalResolver = XRCamera.RenderContextPostProcessPipelineResolver;
        _onRenderThread = false;
        XRCamera.RenderContextPostProcessPipelineResolver = camera =>
            _onRenderThread && ReferenceEquals(camera, _renderedCamera) ? _renderingPipeline : null;
    }

    [TearDown]
    public void TearDown()
        => XRCamera.RenderContextPostProcessPipelineResolver = _originalResolver;

    private static ColorGradingSettings Grading(PipelinePostProcessState state)
    {
        state.GetStage<ColorGradingSettings>()!.TryGetBacking(out ColorGradingSettings? grading).ShouldBeTrue();
        return grading!;
    }

    // The default pipeline that a camera makes for itself on a host. Tests have no host, so it is set directly.
    private static void SetOwnDefaultPipeline(XRCamera camera, RenderPipeline pipeline)
        => typeof(XRCamera).GetField("_defaultRenderPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(camera, pipeline);

    private PipelinePostProcessState RenderQuery(XRCamera camera, RenderPipeline pipeline)
    {
        _renderedCamera = camera;
        _renderingPipeline = pipeline;
        _onRenderThread = true;
        try
        {
            return camera.GetPostProcessState(null)!;
        }
        finally
        {
            _onRenderThread = false;
        }
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
    public void ClaimedCamera_RenderAndUpdateQueriesInTurn_KeepOneGradingObjectAndItsReset()
    {
        DefaultRenderPipeline own = new(stereo: false);
        DefaultRenderPipeline kept = new(stereo: false);
        XRCamera camera = new();
        SetOwnDefaultPipeline(camera, own);
        camera.NoteClaimBorrowedPipeline(kept);

        ColorGradingSettings grading = Grading(RenderQuery(camera, kept));
        grading.ResetAutoExposureHistory();

        for (int frame = 0; frame < 3; frame++)
        {
            PipelinePostProcessState updateState = camera.GetPostProcessState(null)!;
            PipelinePostProcessState renderState = RenderQuery(camera, kept);

            updateState.ShouldBeSameAs(renderState);
            renderState.PipelineId.ShouldBe(kept.ID);
            Grading(renderState).ShouldBeSameAs(grading);
        }

        grading.IsAutoExposureResetPending.ShouldBeTrue();
    }

    [Test]
    public void UnclaimedCamera_KeepsItsDefaultStateOnItsOwnPipeline()
    {
        DefaultRenderPipeline own = new(stereo: false);
        DefaultRenderPipeline other = new(stereo: false);
        XRCamera camera = new();
        SetOwnDefaultPipeline(camera, own);

        PipelinePostProcessState defaultState = camera.GetPostProcessState(null)!;
        PipelinePostProcessState otherState = RenderQuery(camera, other);
        PipelinePostProcessState ownRender = RenderQuery(camera, own);

        defaultState.PipelineId.ShouldBe(own.ID);
        camera.PostProcessStates.DefaultState.ShouldBeSameAs(defaultState);
        ownRender.ShouldBeSameAs(defaultState);
        otherState.ShouldNotBeSameAs(defaultState);
        otherState.PipelineId.ShouldBe(other.ID);
        camera.GetPostProcessState(null).ShouldBeSameAs(defaultState);
        defaultState.PipelineId.ShouldBe(own.ID);
    }

    [Test]
    public void ResetAutoExposureHistory_OnACameraWithNoState_WaitsAndCreatesNothing_ThenTheFirstRenderedStateGetsIt()
    {
        DefaultRenderPipeline kept = new(stereo: false);
        XRCamera camera = new();
        camera.NoteClaimBorrowedPipeline(kept);

        camera.ResetAutoExposureHistory();

        camera.PostProcessStates.DefaultState.ShouldBeNull();
        camera.PostProcessStates.IsAutoExposureResetWaiting.ShouldBeTrue();

        ColorGradingSettings grading = Grading(RenderQuery(camera, kept));

        grading.IsAutoExposureResetPending.ShouldBeTrue();
        camera.PostProcessStates.IsAutoExposureResetWaiting.ShouldBeFalse();
    }

    [Test]
    public void ClaimRelease_ClearsTheShotCameraReset_AndResetsTheReturningCamera()
    {
        DefaultRenderPipeline gameplayPipeline = new(stereo: false);
        DefaultRenderPipeline kept = new(stereo: false);
        CameraComponent gameplay = new SceneNode("Gameplay").AddComponent<CameraComponent>()!;
        CameraComponent shot = new SceneNode("Shot").AddComponent<CameraComponent>()!;
        SetOwnDefaultPipeline(gameplay.Camera, gameplayPipeline);
        ColorGradingSettings gameplayGrading = Grading(gameplay.Camera.GetPostProcessState(null)!);
        shot.Camera.NoteClaimBorrowedPipeline(kept);
        ColorGradingSettings shotGrading = Grading(RenderQuery(shot.Camera, kept));
        XRViewport viewport = new(null) { SetRenderPipelineFromCamera = false };
        viewport.CameraComponent = gameplay;
        object owner = new();

        viewport.ClaimCamera(owner, shot);
        shot.Camera.ResetAutoExposureHistory();
        shotGrading.IsAutoExposureResetPending.ShouldBeTrue();
        gameplayGrading.IsAutoExposureResetPending.ShouldBeFalse();

        viewport.ReleaseCameraClaim(owner);

        viewport.CameraComponent.ShouldBeSameAs(gameplay);
        shotGrading.IsAutoExposureResetPending.ShouldBeFalse();
        gameplayGrading.IsAutoExposureResetPending.ShouldBeTrue();
    }
}
