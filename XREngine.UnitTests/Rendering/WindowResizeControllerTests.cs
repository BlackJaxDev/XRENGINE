using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Runtime.InputIntegration;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class WindowResizeControllerTests
{
    [Test]
    public void VulkanSwapchainRecreation_DoesNotPumpNativeWindowEvents()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");

        source.ShouldNotContain("Window.DoEvents()");
    }

    [Test]
    public void TryConsumeLatestNativeSnapshot_ReturnsNewestSnapshotOnlyOnce()
    {
        WindowResizeController controller = new();
        WindowSurfaceSnapshot first = CreateSnapshot(1, 800, 600);
        WindowSurfaceSnapshot second = CreateSnapshot(2, 1024, 768);

        controller.PublishNativeSnapshot(first);
        controller.PublishNativeSnapshot(second);

        controller.TryConsumeLatestNativeSnapshot(
            out WindowSurfaceSnapshot consumed,
            out WindowResizeExtents extents).ShouldBeTrue();
        consumed.Sequence.ShouldBe(2UL);
        extents.NativeClientExtent.ShouldBe(new Vector2D<int>(1024, 768));
        controller.LastConsumedNativeSnapshotSequence.ShouldBe(2UL);
        controller.DroppedNativeSnapshotCount.ShouldBe(1UL);

        controller.TryConsumeLatestNativeSnapshot(out _, out _).ShouldBeFalse();
    }

    [Test]
    public void PublishNativeSnapshot_ReportsSequenceGapsAsDroppedSnapshots()
    {
        WindowResizeController controller = new();

        controller.PublishNativeSnapshot(CreateSnapshot(1, 800, 600));
        controller.TryConsumeLatestNativeSnapshot(out _, out _).ShouldBeTrue();
        controller.PublishNativeSnapshot(CreateSnapshot(4, 1280, 720));

        controller.DroppedNativeSnapshotCount.ShouldBe(2UL);
        controller.LastNativeSnapshotSequence.ShouldBe(4UL);
    }

    [Test]
    public void SetPresentationAndOutputExtent_DoesNotChangeFullInternalExtent()
    {
        WindowResizeController controller = new();

        WindowResizeExtents initial = controller.SetAllRenderExtents(new Vector2D<int>(800, 600));
        initial.FullInternalExtent.ShouldBe(new Vector2D<int>(800, 600));

        WindowResizeExtents liveResize = controller.SetPresentationAndOutputExtent(new Vector2D<int>(960, 540));

        liveResize.PresentationExtent.ShouldBe(new Vector2D<int>(960, 540));
        liveResize.PipelineOutputExtent.ShouldBe(new Vector2D<int>(960, 540));
        liveResize.FullInternalExtent.ShouldBe(new Vector2D<int>(800, 600));
    }

    [Test]
    public void SetFullInternalExtent_KeepsPresentationAndOutputExtentsSeparate()
    {
        WindowResizeController controller = new();

        controller.SetPresentationAndOutputExtent(new Vector2D<int>(960, 540));
        WindowResizeExtents extents = controller.SetFullInternalExtent(new Vector2D<int>(1280, 720));

        extents.PresentationExtent.ShouldBe(new Vector2D<int>(960, 540));
        extents.PipelineOutputExtent.ShouldBe(new Vector2D<int>(960, 540));
        extents.FullInternalExtent.ShouldBe(new Vector2D<int>(1280, 720));
    }

    [Test]
    public void NeedsFullInternalResize_ReturnsFalseForAlreadyAppliedSnapshot()
    {
        WindowResizeController controller = new();
        WindowSurfaceSnapshot snapshot = CreateSnapshot(1, 1920, 1080);

        controller.PublishNativeSnapshot(snapshot);
        controller.SetAllRenderExtents(snapshot.FramebufferExtent);
        controller.TryConsumeLatestNativeSnapshot(
            out WindowSurfaceSnapshot consumed,
            out WindowResizeExtents extents).ShouldBeTrue();

        WindowResizeController.NeedsFullInternalResize(consumed, extents).ShouldBeFalse();
    }

    [Test]
    public void NeedsFullInternalResize_ReturnsTrueForStaleFullInternalExtent()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(1280, 720));
        WindowSurfaceSnapshot snapshot = CreateSnapshot(1, 1920, 1080);

        controller.PublishNativeSnapshot(snapshot);
        controller.TryConsumeLatestNativeSnapshot(
            out WindowSurfaceSnapshot consumed,
            out WindowResizeExtents extents).ShouldBeTrue();

        WindowResizeController.NeedsFullInternalResize(consumed, extents).ShouldBeTrue();
    }

    [Test]
    public void RequestFullInternalExtent_CoalescesUntilPolicyAllowsAnotherGeneration()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));
        controller.SetPolicy(new WindowFullInternalResizePolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10),
            0.50f));

        WindowResizeExtents first = controller.RequestFullInternalExtent(
            new Vector2D<int>(900, 600),
            force: false,
            timestampTicks: 100,
            out bool firstAccepted);
        WindowResizeExtents second = controller.RequestFullInternalExtent(
            new Vector2D<int>(900, 600),
            force: false,
            timestampTicks: 100 + System.Diagnostics.Stopwatch.Frequency / 10,
            out bool secondAccepted);

        firstAccepted.ShouldBeTrue();
        secondAccepted.ShouldBeFalse();
        first.PendingFullInternalGeneration.ShouldBe(1UL);
        second.PendingFullInternalGeneration.ShouldBe(1UL);
        second.PendingFullInternalExtent.ShouldBe(new Vector2D<int>(900, 600));
    }

    [Test]
    public void RequestFullInternalExtent_ForcedDuplicateKeepsExistingPendingGeneration()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));

        WindowResizeExtents first = controller.RequestFullInternalExtent(
            new Vector2D<int>(1000, 700),
            force: true,
            timestampTicks: 1,
            out bool firstAccepted);
        WindowResizeExtents duplicate = controller.RequestFullInternalExtent(
            new Vector2D<int>(1000, 700),
            force: true,
            timestampTicks: 2,
            out bool duplicateAccepted);

        firstAccepted.ShouldBeTrue();
        duplicateAccepted.ShouldBeFalse();
        duplicate.PendingFullInternalGeneration.ShouldBe(first.PendingFullInternalGeneration);
        duplicate.PendingFullInternalExtent.ShouldBe(first.PendingFullInternalExtent);
        controller.IsStaleFullInternalGeneration(first.PendingFullInternalGeneration).ShouldBeFalse();
    }

    [Test]
    public void RequestFullInternalExtent_RejectedLiveExtentDoesNotReplaceAdmittedTarget()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));
        controller.SetPolicy(new WindowFullInternalResizePolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10),
            0.50f));

        WindowResizeExtents admitted = controller.RequestFullInternalExtent(
            new Vector2D<int>(900, 600),
            force: false,
            timestampTicks: 100,
            out bool admittedAccepted);
        WindowResizeExtents rejected = controller.RequestFullInternalExtent(
            new Vector2D<int>(950, 600),
            force: false,
            timestampTicks: 100 + System.Diagnostics.Stopwatch.Frequency / 10,
            out bool rejectedAccepted);

        admittedAccepted.ShouldBeTrue();
        rejectedAccepted.ShouldBeFalse();
        rejected.PendingFullInternalGeneration.ShouldBe(admitted.PendingFullInternalGeneration);
        rejected.PendingFullInternalExtent.ShouldBe(admitted.PendingFullInternalExtent);
    }

    [Test]
    public void RequestFullInternalExtent_RejectsStalePendingGeneration()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));

        WindowResizeExtents first = controller.RequestFullInternalExtent(
            new Vector2D<int>(900, 600),
            force: true,
            timestampTicks: 1,
            out bool firstAccepted);
        WindowResizeExtents second = controller.RequestFullInternalExtent(
            new Vector2D<int>(1000, 700),
            force: true,
            timestampTicks: 2,
            out bool secondAccepted);

        firstAccepted.ShouldBeTrue();
        secondAccepted.ShouldBeTrue();
        controller.IsStaleFullInternalGeneration(first.PendingFullInternalGeneration).ShouldBeTrue();
        controller.IsStaleFullInternalGeneration(second.PendingFullInternalGeneration).ShouldBeFalse();
    }

    [Test]
    public void SetFullInternalExtent_CommitsPendingGenerationAtomically()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));
        controller.RequestFullInternalExtent(
            new Vector2D<int>(1000, 700),
            force: true,
            timestampTicks: 1,
            out _);

        WindowResizeExtents committed = controller.SetFullInternalExtent(new Vector2D<int>(1000, 700));

        committed.FullInternalExtent.ShouldBe(new Vector2D<int>(1000, 700));
        committed.PendingFullInternalExtent.ShouldBe(default);
        committed.PendingFullInternalGeneration.ShouldBe(0UL);
        committed.FullInternalGeneration.ShouldBeGreaterThan(0UL);
    }

    [Test]
    public void TryCommitPendingFullInternalExtent_RequiresCurrentPendingGeneration()
    {
        WindowResizeController controller = new();
        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));
        WindowResizeExtents requested = controller.RequestFullInternalExtent(
            new Vector2D<int>(1000, 700),
            force: true,
            timestampTicks: 1,
            out bool accepted);

        accepted.ShouldBeTrue();
        controller.TryCommitPendingFullInternalExtent(
            requested.PendingFullInternalGeneration + 1,
            requested.PendingFullInternalExtent,
            out WindowResizeExtents staleCommit).ShouldBeFalse();
        staleCommit.PendingFullInternalGeneration.ShouldBe(requested.PendingFullInternalGeneration);

        controller.TryCommitPendingFullInternalExtent(
            requested.PendingFullInternalGeneration,
            requested.PendingFullInternalExtent,
            out WindowResizeExtents committed).ShouldBeTrue();

        committed.FullInternalExtent.ShouldBe(new Vector2D<int>(1000, 700));
        committed.PendingFullInternalExtent.ShouldBe(default);
        committed.PendingFullInternalGeneration.ShouldBe(0UL);
    }

    [Test]
    public void OutputScale_ReportsExactUpscaleAndLetterboxModes()
    {
        WindowResizeController controller = new();

        controller.SetAllRenderExtents(new Vector2D<int>(800, 600));
        controller.OutputScale.Mode.ShouldBe(WindowOutputScaleMode.Exact);

        controller.SetPresentationAndOutputExtent(new Vector2D<int>(1600, 1200));
        controller.OutputScale.Mode.ShouldBe(WindowOutputScaleMode.Upscale);
        controller.OutputScale.ScaleX.ShouldBe(2.0f);
        controller.OutputScale.ScaleY.ShouldBe(2.0f);

        controller.SetPresentationAndOutputExtent(new Vector2D<int>(1600, 900));
        controller.OutputScale.Mode.ShouldBe(WindowOutputScaleMode.Pillarbox);
    }

    [Test]
    public void WindowInputSnapshotAccumulator_PublishesOrderedTransitionsAndResetsDeltasAfterConsumption()
    {
        WindowInputSnapshotAccumulator accumulator = new();

        accumulator.RecordKeyDown(EKey.W);
        accumulator.RecordTextInput('w');
        accumulator.RecordMouseDown(EMouseButton.LeftClick);
        accumulator.PrimePointerPosition(10.0f, 20.0f);
        accumulator.RecordPointerPosition(13.0f, 25.0f);
        accumulator.RecordPointerPosition(20.0f, 30.0f);
        accumulator.RecordScroll(1.5f, -2.0f);
        accumulator.RecordKeyUp(EKey.W);
        accumulator.RecordMouseUp(EMouseButton.LeftClick);

        WindowInputSnapshot first = accumulator.Publish(
            keyboardCount: 1,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        first.Sequence.ShouldBe(1UL);
        first.KeyboardCount.ShouldBe(1);
        first.MouseCount.ShouldBe(1);
        first.IsFocused.ShouldBeTrue();
        first.PointerX.ShouldBe(20.0f);
        first.PointerY.ShouldBe(30.0f);
        first.PointerDeltaX.ShouldBe(10.0f);
        first.PointerDeltaY.ShouldBe(10.0f);
        first.ScrollDeltaX.ShouldBe(1.5f);
        first.ScrollDeltaY.ShouldBe(-2.0f);
        first.KeyDownTransitionCount.ShouldBe(1U);
        first.KeyUpTransitionCount.ShouldBe(1U);
        first.MouseDownTransitionCount.ShouldBe(1U);
        first.MouseUpTransitionCount.ShouldBe(1U);
        first.TextInputCount.ShouldBe(1U);
        first.KeyTransitions.ShouldBe(new[]
        {
            new WindowKeyTransition(EKey.W, true),
            new WindowKeyTransition(EKey.W, false),
        });
        first.PressedKeys.ShouldBeEmpty();
        first.TextInputCharacters.ShouldBe(new[] { 'w' });
        first.MouseButtonTransitions.ShouldBe(new[]
        {
            new WindowMouseButtonTransition(EMouseButton.LeftClick, true),
            new WindowMouseButtonTransition(EMouseButton.LeftClick, false),
        });
        first.PressedMouseButtons.ShouldBeEmpty();
        accumulator.Latest.ShouldBe(first);
        accumulator.ConsumeLatest().ShouldBe(first);

        WindowInputSnapshot second = accumulator.Publish(
            keyboardCount: 1,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        second.Sequence.ShouldBe(2UL);
        second.PointerX.ShouldBe(20.0f);
        second.PointerY.ShouldBe(30.0f);
        second.PointerDeltaX.ShouldBe(0.0f);
        second.PointerDeltaY.ShouldBe(0.0f);
        second.ScrollDeltaX.ShouldBe(0.0f);
        second.ScrollDeltaY.ShouldBe(0.0f);
        second.KeyDownTransitionCount.ShouldBe(1U);
        second.KeyUpTransitionCount.ShouldBe(1U);
        second.MouseDownTransitionCount.ShouldBe(1U);
        second.MouseUpTransitionCount.ShouldBe(1U);
        second.TextInputCount.ShouldBe(1U);
        second.KeyTransitions.ShouldBeEmpty();
        second.PressedKeys.ShouldBeEmpty();
        second.TextInputCharacters.ShouldBeEmpty();
        second.MouseButtonTransitions.ShouldBeEmpty();
        second.PressedMouseButtons.ShouldBeEmpty();
    }

    [Test]
    public void WindowInputSnapshotAccumulator_RetainsScrollAcrossPublicationsAndConsumesItExactlyOnce()
    {
        WindowInputSnapshotAccumulator accumulator = new();
        WindowSnapshotMouse mouse = new(0);
        int scrollEventCount = 0;
        float totalScroll = 0.0f;
        mouse.RegisterScroll(delta =>
        {
            scrollEventCount++;
            totalScroll += delta;
        }, unregister: false);

        accumulator.RecordScroll(0.0f, -1.0f);
        _ = accumulator.Publish(
            keyboardCount: 0,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        accumulator.RecordScroll(0.0f, -2.0f);
        _ = accumulator.Publish(
            keyboardCount: 0,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        WindowInputSnapshot latest = accumulator.Publish(
            keyboardCount: 0,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        latest.Sequence.ShouldBe(3UL);
        latest.ScrollDeltaY.ShouldBe(-3.0f);

        WindowInputSnapshot consumed = accumulator.ConsumeLatest();
        consumed.ShouldBe(latest);
        mouse.ApplySnapshot(consumed);
        mouse.TickStates(1.0f / 60.0f);
        scrollEventCount.ShouldBe(1);
        totalScroll.ShouldBe(-3.0f);

        WindowInputSnapshot alreadyConsumed = accumulator.ConsumeLatest();
        alreadyConsumed.Sequence.ShouldBe(3UL);
        alreadyConsumed.ScrollDeltaY.ShouldBe(0.0f);
        mouse.ApplySnapshot(alreadyConsumed);
        mouse.TickStates(1.0f / 60.0f);
        scrollEventCount.ShouldBe(1);

        WindowInputSnapshot next = accumulator.Publish(
            keyboardCount: 0,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);
        next.ScrollDeltaY.ShouldBe(0.0f);
        mouse.ApplySnapshot(accumulator.ConsumeLatest());
        mouse.TickStates(1.0f / 60.0f);
        scrollEventCount.ShouldBe(1);
    }

    [Test]
    public void WindowInputSnapshotAccumulator_FirstPointerMovePrimesPositionWithoutDelta()
    {
        WindowInputSnapshotAccumulator accumulator = new();

        accumulator.RecordPointerPosition(4.0f, 5.0f);

        WindowInputSnapshot snapshot = accumulator.Publish(
            keyboardCount: 0,
            mouseCount: 1,
            gamepadCount: 0,
            isFocused: false,
            isMouseCaptured: true);

        snapshot.Sequence.ShouldBe(1UL);
        snapshot.HasMouse.ShouldBeTrue();
        snapshot.IsMouseCaptured.ShouldBeTrue();
        snapshot.PointerX.ShouldBe(4.0f);
        snapshot.PointerY.ShouldBe(5.0f);
        snapshot.PointerDeltaX.ShouldBe(0.0f);
        snapshot.PointerDeltaY.ShouldBe(0.0f);
    }

    [Test]
    public void WindowSnapshotKeyboard_ReplaysPressedSnapshotIntoRegisteredKeyState()
    {
        WindowSnapshotKeyboard keyboard = new(0);
        bool? latestPressedState = null;
        keyboard.RegisterKeyPressed(EKey.W, pressed => latestPressedState = pressed, unregister: false);

        WindowInputSnapshotAccumulator accumulator = new();
        accumulator.RecordKeyDown(EKey.W);
        WindowInputSnapshot downSnapshot = accumulator.Publish(
            keyboardCount: 1,
            mouseCount: 0,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        keyboard.ApplySnapshot(downSnapshot);
        keyboard.TickStates(1.0f / 60.0f);

        latestPressedState.ShouldBe(true);
        keyboard.GetKeyState(EKey.W, EButtonInputType.Pressed).ShouldBeTrue();

        accumulator.RecordKeyUp(EKey.W);
        WindowInputSnapshot upSnapshot = accumulator.Publish(
            keyboardCount: 1,
            mouseCount: 0,
            gamepadCount: 0,
            isFocused: true,
            isMouseCaptured: false);

        keyboard.ApplySnapshot(upSnapshot);
        keyboard.TickStates(1.0f / 60.0f);

        latestPressedState.ShouldBe(false);
        keyboard.GetKeyState(EKey.W, EButtonInputType.Released).ShouldBeTrue();
    }

    private static WindowSurfaceSnapshot CreateSnapshot(ulong sequence, int width, int height)
        => new(
            sequence,
            width,
            height,
            width,
            height,
            1.0f,
            1.0f,
            false,
            true,
            (long)sequence);

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "XRENGINE.slnx");
            if (File.Exists(candidate))
            {
                string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
                return File.ReadAllText(path);
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
