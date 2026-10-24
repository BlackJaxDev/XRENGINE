using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class AnimationPlotFrameAxisTests
{
    [TestCase(0.26f, 10, 2f, 0.3f)]
    [TestCase(0.24f, 10, 2f, 0.2f)]
    [TestCase(0.25f, 10, 2f, 0.3f)]
    [TestCase(-1f, 30, 2f, 0f)]
    [TestCase(5f, 30, 2f, 2f)]
    public void TimesSnapToTheNearestFrameInsideTheClip(float second, int rate, float length, float expected)
        => AnimationPlotFrameAxis.SnapToFrame(second, rate, length).ShouldBe(expected, 0.0001f);

    [Test]
    public void SnapDoesNotPassAClipEndBetweenFrames()
        // 1.05 s at 10 fps rounds to frame 11 (1.1 s), past the 1.07 s clip end.
        => AnimationPlotFrameAxis.SnapToFrame(1.05f, 10, 1.07f).ShouldBe(1.07f, 0.0001f);

    [Test]
    public void WithoutASampleRateOnlyTheClampApplies()
    {
        AnimationPlotFrameAxis.SnapToFrame(0.123f, 0, 2).ShouldBe(0.123f, 0.0001f);
        AnimationPlotFrameAxis.SnapToFrame(float.NaN, 30, 2).ShouldBe(0f);
        AnimationPlotFrameAxis.SnapToFrame(1, 30, 0).ShouldBe(0f);
    }

    [TestCase(100f, 190f, true)]
    [TestCase(100f, 170f, true)]
    [TestCase(100f, 169f, false)]
    [TestCase(7f, 190f, false)]
    [TestCase(209f, 190f, false)]
    [TestCase(100f, 201f, false)]
    public void OnlyTheBandUnderTheLanesIsTheAxis(float x, float y, bool expected)
        => AnimationPlotFrameAxis.IsOnAxis(216, 200, x, y).ShouldBe(expected);

    [Test]
    public void PlotsShorterThanTheStripHaveNoAxis()
        => AnimationPlotFrameAxis.IsOnAxis(216, 20, 100, 10).ShouldBeFalse();
}
