using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class WindowResizeControllerTests
{
    [Test]
    public void VulkanSwapchainRecreation_DoesNotPumpNativeWindowEvents()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/SwapChain.cs");

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
