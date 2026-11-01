using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneClockTests
{
    private const double Frame = 1.0 / 60.0;

    [Test]
    public void Advance_AtRateTwo_MovesByTwiceTheDelta()
    {
        CutsceneClock clock = new() { Rate = 2.0 };
        clock.Advance(0.5);
        clock.Position.ShouldBe(CutsceneTime.FromSeconds(1.0));
    }

    [Test]
    public void Advance_AtNegativeRate_MovesBackward()
    {
        CutsceneClock clock = new() { Rate = -1.0 };
        clock.SetPosition(CutsceneTime.FromSeconds(2.0));
        clock.Advance(0.5);
        clock.Position.ShouldBe(CutsceneTime.FromSeconds(1.5));
    }

    [Test]
    public void Advance_InManualMode_IsIgnored()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.Manual };
        clock.Advance(1.0);
        clock.Position.ShouldBe(CutsceneTime.Zero);
    }

    [Test]
    public void SetPosition_InManualMode_SetsPosition()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.Manual };
        clock.SetPosition(CutsceneTime.FromSeconds(3.0));
        clock.Position.ShouldBe(CutsceneTime.FromSeconds(3.0));
    }

    [Test]
    public void Discipline_SmallDifference_CorrectsByAtMostFivePercentOfDelta()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.AudioDisciplined };
        clock.Advance(Frame);
        CutsceneTime before = clock.Position;
        CutsceneTime audio = before + CutsceneTime.FromSeconds(0.010);

        clock.Discipline(audio);

        long moved = clock.Position.Flicks - before.Flicks;
        moved.ShouldBeGreaterThan(0L);
        moved.ShouldBeLessThanOrEqualTo(CutsceneTime.FromSeconds(Frame * CutsceneClock.MaxSlewFraction).Flicks);
        clock.ConsumeHardResync().ShouldBeFalse();
    }

    [Test]
    public void Discipline_SmallDifference_CorrectsTowardAudioFromAbove()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.AudioDisciplined };
        clock.Advance(Frame);
        CutsceneTime before = clock.Position;

        clock.Discipline(before - CutsceneTime.FromSeconds(0.010));

        clock.Position.ShouldBeLessThan(before);
    }

    [Test]
    public void Discipline_TinyDifference_DoesNotOvershoot()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.AudioDisciplined };
        clock.Advance(1.0);
        CutsceneTime audio = clock.Position + new CutsceneTime(5);

        clock.Discipline(audio);

        clock.Position.ShouldBe(audio);
    }

    [Test]
    public void Discipline_LargeDifference_ResyncsAtOnceAndFlagsOnce()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.AudioDisciplined };
        clock.Advance(Frame);
        CutsceneTime audio = clock.Position + CutsceneTime.FromSeconds(0.150);

        clock.Discipline(audio);

        clock.Position.ShouldBe(audio);
        clock.ConsumeHardResync().ShouldBeTrue();
        clock.ConsumeHardResync().ShouldBeFalse();
    }

    [Test]
    public void Discipline_OutsideAudioDisciplinedMode_IsIgnored()
    {
        CutsceneClock clock = new();
        clock.Advance(Frame);
        CutsceneTime before = clock.Position;

        clock.Discipline(before + CutsceneTime.FromSeconds(0.500));

        clock.Position.ShouldBe(before);
        clock.ConsumeHardResync().ShouldBeFalse();
    }

    [Test]
    public void Advance_InAudioDisciplinedMode_MovesPosition()
    {
        CutsceneClock clock = new() { Mode = ECutsceneClockMode.AudioDisciplined };
        clock.Advance(0.25);
        clock.Position.ShouldBe(CutsceneTime.FromSeconds(0.25));
    }
}
