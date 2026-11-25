using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class AnimationPlotValueZoomTests
{
    private const float Height = 236; // 200 value pixels between the insets.

    [TestCase(12f)]
    [TestCase(62f)]
    [TestCase(162f)]
    public void ZoomKeepsTheValueUnderThePointer(float pointerY)
    {
        // A quarter of the range centered at 0.5, which shows 0.375 to 0.625.
        float before = FractionAt(4, 0.5f, pointerY);

        AnimationPlotValueZoom.TryZoom(4, 0.5f, Height, pointerY, 1,
            out float zoom, out float center).ShouldBeTrue();

        zoom.ShouldBe(4 * AnimationPlotWheelZoom.StepFactor, 0.0001f);
        FractionAt(zoom, center, pointerY).ShouldBe(before, 0.0001f);
    }

    [Test]
    public void ZoomOutNearAnEdgeClampsTheWindowToTheRange()
    {
        // The window shows the top half; the pointer is at its low edge.
        AnimationPlotValueZoom.TryZoom(2, 0.75f, Height, AnimationPlotValueZoom.BottomInset, -1,
            out float zoom, out float center).ShouldBeTrue();
        zoom.ShouldBe(2 / AnimationPlotWheelZoom.StepFactor, 0.0001f);
        (center + 0.5f / zoom).ShouldBe(1, 0.0001f);

        AnimationPlotValueZoom.TryZoom(1.1f, 0.5f, Height, 100, -4, out zoom, out center).ShouldBeTrue();
        zoom.ShouldBe(AnimationPlotValueZoom.MinimumZoom);
        center.ShouldBe(AnimationPlotValueZoom.DefaultCenter, 0.0001f);
    }

    [Test]
    public void LimitsAndInvalidInputKeepTheView()
    {
        AnimationPlotValueZoom.TryZoom(AnimationPlotValueZoom.MaximumZoom, 0.3f, Height, 100, 1,
            out float zoom, out float center).ShouldBeTrue();
        zoom.ShouldBe(AnimationPlotValueZoom.MaximumZoom);
        center.ShouldBe(0.3f);

        AnimationPlotValueZoom.TryZoom(2, 0.5f, Height, float.NaN, 1, out _, out _).ShouldBeFalse();
        AnimationPlotValueZoom.TryZoom(2, 0.5f, Height, 100, 0, out _, out _).ShouldBeFalse();
        AnimationPlotValueZoom.TryZoom(2, 0.5f, AnimationPlotValueZoom.VerticalInsets, 100, 1, out _, out _).ShouldBeFalse();
    }

    private static float FractionAt(float zoom, float center, float pointerY)
        => AnimationPlotValueZoom.WindowStart(zoom, center) +
            (pointerY - AnimationPlotValueZoom.BottomInset) /
            (Height - AnimationPlotValueZoom.VerticalInsets) / zoom;
}
