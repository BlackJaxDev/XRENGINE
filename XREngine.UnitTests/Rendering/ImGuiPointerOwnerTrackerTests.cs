using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ImGuiPointerOwnerTrackerTests
{
    private const uint PopupViewport = 42;

    [Test]
    public void MainWindowOwnsPointer_ByDefault()
    {
        ImGuiPointerOwnerTracker tracker = new();

        tracker.BeginFrame();
        tracker.UpdateMainPointer(10.0f, 20.0f).ShouldBeTrue();
    }

    [Test]
    public void StaleMainPointer_DoesNotTakeOwnershipFromPopup()
    {
        ImGuiPointerOwnerTracker tracker = new();
        tracker.BeginFrame();
        tracker.UpdateMainPointer(10.0f, 20.0f);

        tracker.BeginFrame();
        tracker.NotePlatformPointerActivity(PopupViewport);
        tracker.UpdateMainPointer(10.0f, 20.0f).ShouldBeFalse();

        // The pointer rests in the popup window: no new events from any window.
        tracker.BeginFrame();
        tracker.UpdateMainPointer(10.0f, 20.0f).ShouldBeFalse();
        tracker.OwnerViewportId.ShouldBe(PopupViewport);
    }

    [Test]
    public void MainPointerMovement_ReturnsOwnershipToMainWindow()
    {
        ImGuiPointerOwnerTracker tracker = new();
        tracker.BeginFrame();
        tracker.UpdateMainPointer(10.0f, 20.0f);
        tracker.BeginFrame();
        tracker.NotePlatformPointerActivity(PopupViewport);
        tracker.UpdateMainPointer(10.0f, 20.0f);

        tracker.BeginFrame();
        tracker.UpdateMainPointer(11.0f, 20.0f).ShouldBeTrue();
    }

    [Test]
    public void PlatformActivity_WinsWhenBothWindowsReportInOneFrame()
    {
        ImGuiPointerOwnerTracker tracker = new();
        tracker.BeginFrame();
        tracker.UpdateMainPointer(10.0f, 20.0f);

        tracker.BeginFrame();
        tracker.NotePlatformPointerActivity(PopupViewport);
        tracker.UpdateMainPointer(12.0f, 20.0f).ShouldBeFalse();
    }

    [Test]
    public void MainButtonActivity_ReturnsOwnershipWhenMainPointerDidNotMove()
    {
        ImGuiPointerOwnerTracker tracker = new();
        tracker.BeginFrame();
        tracker.NotePlatformPointerActivity(PopupViewport);
        tracker.UpdateMainPointer(10.0f, 20.0f).ShouldBeFalse();

        tracker.BeginFrame();
        tracker.UpdateMainPointer(10.0f, 20.0f).ShouldBeFalse();

        tracker.BeginFrame();
        tracker.NoteMainPointerActivity();
        tracker.UpdateMainPointer(10.0f, 20.0f).ShouldBeTrue();
    }

    [Test]
    public void ReleasedPlatformWindow_ReturnsOwnershipToMainWindow()
    {
        ImGuiPointerOwnerTracker tracker = new();
        tracker.BeginFrame();
        tracker.NotePlatformPointerActivity(PopupViewport);

        tracker.ReleasePlatformWindow(PopupViewport);

        tracker.MainWindowOwnsPointer.ShouldBeTrue();
    }
}
