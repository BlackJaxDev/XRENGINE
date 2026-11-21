using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneTimeTests
{
    [Test]
    public void FromFrames_At24Fps_UsesExactFlicks()
        => CutsceneTime.FromFrames(1, 24).Flicks.ShouldBe(29_400_000L);

    [Test]
    public void FromSamples_At44100_UsesExactFlicks()
        => CutsceneTime.FromSamples(1, 44100).Flicks.ShouldBe(16_000L);

    [Test]
    public void FromSamples_At48000_UsesExactFlicks()
        => CutsceneTime.FromSamples(1, 48000).Flicks.ShouldBe(14_700L);

    [Test]
    public void ToSamples_OneHourAt48000_IsExact()
        => CutsceneTime.FromSeconds(3600).ToSamples(48000).ShouldBe(172_800_000L);

    [Test]
    public void FromSeconds_RoundTripsThroughToSeconds()
        => CutsceneTime.FromSeconds(1.5).ToSeconds().ShouldBe(1.5);

    [Test]
    public void Clamp_ReturnsMinimumBelowRange()
    {
        CutsceneTime min = new(100);
        CutsceneTime max = new(200);
        new CutsceneTime(50).Clamp(min, max).ShouldBe(min);
    }

    [Test]
    public void Clamp_ReturnsMaximumAboveRange()
    {
        CutsceneTime min = new(100);
        CutsceneTime max = new(200);
        new CutsceneTime(250).Clamp(min, max).ShouldBe(max);
    }

    [Test]
    public void Clamp_KeepsValueInsideRange()
    {
        CutsceneTime value = new(150);
        value.Clamp(new CutsceneTime(100), new CutsceneTime(200)).ShouldBe(value);
    }

    [Test]
    public void Operators_AddSubtractAndCompare()
    {
        CutsceneTime a = new(10);
        CutsceneTime b = new(4);
        (a + b).Flicks.ShouldBe(14L);
        (a - b).Flicks.ShouldBe(6L);
        (a > b).ShouldBeTrue();
        (b < a).ShouldBeTrue();
        (a >= new CutsceneTime(10)).ShouldBeTrue();
        (b <= a).ShouldBeTrue();
        a.CompareTo(b).ShouldBeGreaterThan(0);
    }
}
