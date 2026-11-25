using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class AnimationPlotWheelZoomTests
{
    private const float Width = 216; // 200 plot pixels between the two insets.

    [TestCase(8f)]
    [TestCase(58f)]
    [TestCase(158f)]
    public void ZoomKeepsTheTimeUnderThePointer(float pointerX)
    {
        // A 2 s window from 2 s on a 10 s clip at zoom 5.
        float before = TimeAt(2, 2, pointerX);

        AnimationPlotWheelZoom.TryZoom(5, 2, 2, 10, Width, pointerX, 1,
            out float zoom, out float start).ShouldBeTrue();

        zoom.ShouldBe(5 * AnimationPlotWheelZoom.StepFactor, 0.0001f);
        TimeAt(start, 10 / zoom, pointerX).ShouldBe(before, 0.0001f);
    }

    [Test]
    public void ZoomOutNearAnEdgeClampsTheWindowToTheClip()
    {
        // The pointer is at the left edge of the last half of the clip.
        AnimationPlotWheelZoom.TryZoom(2, 5, 5, 10, Width, AnimationPlotWheelZoom.HorizontalInset, -1,
            out float zoom, out float start).ShouldBeTrue();

        zoom.ShouldBe(2 / AnimationPlotWheelZoom.StepFactor, 0.0001f);
        start.ShouldBe(10 - 10 / zoom, 0.0001f);

        // The pointer is at the right edge of the first half of the clip.
        AnimationPlotWheelZoom.TryZoom(2, 0, 5, 10, Width, Width - AnimationPlotWheelZoom.HorizontalInset, -1,
            out _, out start).ShouldBeTrue();
        start.ShouldBe(0);
    }

    [Test]
    public void ZoomLimitsKeepTheCurrentView()
    {
        AnimationPlotWheelZoom.TryZoom(1, 0, 10, 10, Width, 50, -3,
            out float zoom, out float start).ShouldBeTrue();
        zoom.ShouldBe(1);
        start.ShouldBe(0);

        AnimationPlotWheelZoom.TryZoom(AnimationPlotWheelZoom.MaximumZoom, 3, 0.3125f, 10,
            Width, 50, 2, out zoom, out start).ShouldBeTrue();
        zoom.ShouldBe(AnimationPlotWheelZoom.MaximumZoom);
        start.ShouldBe(3);
    }

    [TestCase(float.NaN, 1f, 10f)]
    [TestCase(50f, 0f, 10f)]
    [TestCase(50f, float.PositiveInfinity, 10f)]
    [TestCase(50f, 1f, 0f)]
    public void InvalidInputIsRejected(float pointerX, float wheelDelta, float clipLength)
        => AnimationPlotWheelZoom.TryZoom(2, 0, 5, clipLength, Width, pointerX, wheelDelta,
            out _, out _).ShouldBeFalse();

    private static float TimeAt(float start, float duration, float pointerX)
        => start + (pointerX - AnimationPlotWheelZoom.HorizontalInset) /
            (Width - 2 * AnimationPlotWheelZoom.HorizontalInset) * duration;
}
