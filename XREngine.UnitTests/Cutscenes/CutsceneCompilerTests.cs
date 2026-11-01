using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneCompilerTests
{
    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static ActivationClip Clip(double start, double duration, ECutsceneClipExtrapolation extrapolation = ECutsceneClipExtrapolation.None)
        => new() { Start = T(start), Duration = T(duration), Extrapolation = extrapolation };

    private static CutsceneAsset Asset(double duration, params CutsceneClip[][] tracks)
    {
        CutsceneAsset asset = new() { Duration = T(duration) };
        foreach (CutsceneClip[] clips in tracks)
        {
            ActivationTrack track = new();
            track.Clips.AddRange(clips);
            asset.Tracks.Add(track);
        }
        return asset;
    }

    private static CompiledCutscene Compile(CutsceneAsset asset, out List<CutsceneCompileDiagnostic> diagnostics)
    {
        diagnostics = [];
        return CutsceneCompiler.Compile(asset, diagnostics);
    }

    [Test]
    public void EmptyAsset_GivesNoActiveClips()
    {
        CompiledCutscene compiled = Compile(Asset(10), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];
        compiled.QueryActive(T(1), buffer).ShouldBe(0);
        compiled.MaxActiveClips.ShouldBe(0);
    }

    [Test]
    public void StartIsInclusiveAndEndIsExclusive()
    {
        CompiledCutscene compiled = Compile(Asset(10, [Clip(2, 3)]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];
        compiled.QueryActive(T(1.999), buffer).ShouldBe(0);
        compiled.QueryActive(T(2), buffer).ShouldBe(1);
        buffer[0].LocalTime.ShouldBe(T(0));
        compiled.QueryActive(T(4.999), buffer).ShouldBe(1);
        compiled.QueryActive(T(5), buffer).ShouldBe(0);
    }

    [Test]
    public void LocalTime_UsesSpeedAndOffset()
    {
        CutsceneClip clip = Clip(1, 4);
        clip.Speed = 2.0;
        clip.ClipOffset = T(3);
        CompiledCutscene compiled = Compile(Asset(10, [clip]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(2.5), buffer).ShouldBe(1);
        buffer[0].LocalTime.ShouldBe(T(6));
    }

    [Test]
    public void OverlappingClips_GiveTwoResultsWithWeightsThatSumToOne()
    {
        CutsceneClip a = Clip(0, 10);
        a.BlendOut = T(4);
        CutsceneClip b = Clip(6, 10);
        b.BlendIn = T(4);
        CompiledCutscene compiled = Compile(Asset(20, [a], [b]), out _);
        compiled.MaxActiveClips.ShouldBe(2);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];
        compiled.QueryActive(T(8), buffer).ShouldBe(2);
        (buffer[0].Weight + buffer[1].Weight).ShouldBe(1f, 1e-5f);
        buffer[0].Weight.ShouldBe(0.5f, 1e-5f);
    }

    [Test]
    public void ClipWithoutBlend_HasWeightOne()
    {
        CompiledCutscene compiled = Compile(Asset(10, [Clip(0, 5)]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(0), buffer);
        buffer[0].Weight.ShouldBe(1f);
    }

    [Test]
    public void Loop_WrapsLocalTime()
    {
        CutsceneClip clip = Clip(0, 2, ECutsceneClipExtrapolation.Loop);
        CompiledCutscene compiled = Compile(Asset(10, [clip]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(5), buffer).ShouldBe(1);
        buffer[0].LocalTime.ShouldBe(T(1));
        compiled.QueryActive(T(9.5), buffer).ShouldBe(1);
        buffer[0].LocalTime.ShouldBe(T(1.5));
    }

    [Test]
    public void Loop_FarPastTheEnd_DoesNotOverflow()
    {
        CutsceneClip clip = Clip(0, 2, ECutsceneClipExtrapolation.Loop);
        clip.Speed = 3.0;
        CompiledCutscene compiled = Compile(Asset(1_000_000_000, [clip]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(999_999_999.5), buffer).ShouldBe(1);
        buffer[0].LocalTime.Flicks.ShouldBeGreaterThanOrEqualTo(0);
        buffer[0].LocalTime.ShouldBeLessThan(T(6));
    }

    [Test]
    public void Loop_EndsAtNextClipOnTheSameTrack()
    {
        CompiledCutscene compiled = Compile(Asset(20, [Clip(0, 2, ECutsceneClipExtrapolation.Loop), Clip(6, 2)]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];
        compiled.QueryActive(T(5.9), buffer).ShouldBe(1);
        compiled.QueryActive(T(6), buffer).ShouldBe(1);
        buffer[0].ClipIndex.ShouldBe(1);
    }

    [Test]
    public void Hold_StaysActiveAtTheLastLocalTime_UntilAssetEnd()
    {
        CompiledCutscene compiled = Compile(Asset(10, [Clip(0, 2, ECutsceneClipExtrapolation.Hold)]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(7), buffer).ShouldBe(1);
        buffer[0].LocalTime.ShouldBe(T(2));
        buffer[0].Weight.ShouldBe(1f);
        compiled.QueryActive(T(10), buffer).ShouldBe(0);
    }

    [Test]
    public void Hold_EndsWhenTheNextClipOnTheTrackFinishesItsBlendIn()
    {
        CutsceneClip next = Clip(6, 4);
        next.BlendIn = T(2);
        CompiledCutscene compiled = Compile(Asset(20, [Clip(0, 2, ECutsceneClipExtrapolation.Hold), next]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];

        compiled.QueryActive(T(5.9), buffer).ShouldBe(1);
        compiled.QueryActive(T(7.9), buffer).ShouldBe(2);
        compiled.QueryActive(T(8), buffer).ShouldBe(1);
        buffer[0].ClipIndex.ShouldBe(1);
    }

    [Test]
    public void Crossfade_OutgoingWeightIsOneMinusTheIncomingRamp()
    {
        CutsceneClip next = Clip(6, 4);
        next.BlendIn = T(2);
        CompiledCutscene compiled = Compile(Asset(20, [Clip(0, 2, ECutsceneClipExtrapolation.Loop), next]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];

        for (double t = 6.0; t < 8.0; t += 0.25)
        {
            compiled.QueryActive(T(t), buffer).ShouldBe(2);
            (buffer[0].Weight + buffer[1].Weight).ShouldBe(1f, 1e-5f);
        }
        compiled.QueryActive(T(7), buffer);
        buffer[0].Weight.ShouldBe(0.5f, 1e-5f);
    }

    [Test]
    public void TimesOutsideTheAsset_DoNotThrow()
    {
        CompiledCutscene compiled = Compile(Asset(10, [Clip(0, 2, ECutsceneClipExtrapolation.Loop)]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(-5), buffer).ShouldBe(0);
        compiled.QueryActive(new CutsceneTime(long.MaxValue), buffer).ShouldBe(0);
        compiled.QueryActive(new CutsceneTime(long.MinValue), buffer).ShouldBe(0);
    }

    [Test]
    public void ZeroLengthClip_GivesDiagnosticAndNoSegment()
    {
        CompiledCutscene compiled = Compile(Asset(10, [Clip(1, 0)]), out List<CutsceneCompileDiagnostic> diagnostics);
        diagnostics.Count.ShouldBe(1);
        diagnostics[0].TrackIndex.ShouldBe(0);
        diagnostics[0].ClipIndex.ShouldBe(0);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(1), buffer).ShouldBe(0);
        compiled.MaxActiveClips.ShouldBe(0);
    }

    [TestCase(0.0)]
    [TestCase(-1.0)]
    public void NonPositiveSpeed_GivesDiagnostic(double speed)
    {
        CutsceneClip clip = Clip(0, 5);
        clip.Speed = speed;
        CompiledCutscene compiled = Compile(Asset(10, [clip]), out List<CutsceneCompileDiagnostic> diagnostics);
        diagnostics.Count.ShouldBe(1);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[1];
        compiled.QueryActive(T(1), buffer).ShouldBe(0);
    }

    [Test]
    public void SmallDestination_ReceivesWhatFits()
    {
        CompiledCutscene compiled = Compile(Asset(10, [Clip(0, 5)], [Clip(0, 5)], [Clip(0, 5)]), out _);
        compiled.MaxActiveClips.ShouldBe(3);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[2];
        compiled.QueryActive(T(1), buffer).ShouldBe(2);
    }

    [Test]
    public void Query_AllocatesNothing()
    {
        CutsceneClip a = Clip(0, 10, ECutsceneClipExtrapolation.Loop);
        a.BlendIn = T(1);
        CompiledCutscene compiled = Compile(Asset(20, [a], [Clip(2, 5)]), out _);
        Span<CutsceneActiveClip> buffer = stackalloc CutsceneActiveClip[4];
        compiled.QueryActive(T(3), buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            compiled.QueryActive(T(3 + i * 0.01), buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).ShouldBe(0);
    }
}
