using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Data.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimationTimingTests
{
    [Test]
    public void FrameIndexConstructors_PreserveAuthoredFrameIdentity()
    {
        var keyframe = new FloatKeyframe(12, 24.0f, 5.0f, 0.0f, EVectorInterpType.Linear);

        keyframe.AuthoredFrameIndex.ShouldBe(12);
        keyframe.Second.ShouldBe(0.5f, 0.000001f);
    }

    [Test]
    public void FrameCountConstructor_PreservesAuthoredCadenceOnPropertyAnimation()
    {
        var animation = new PropAnimFloat(24, 24.0f, looped: true, useKeyframes: true);

        animation.HasAuthoredCadence.ShouldBeTrue();
        animation.AuthoredFrameCount.ShouldBe(24);
        animation.AuthoredFramesPerSecond.ShouldBe(24);
        animation.LengthInSeconds.ShouldBe(1.0f, 0.000001f);
    }

    [Test]
    public void TickLong_WrapsExactlyAtClipLength()
    {
        var animation = new PropAnimFloat(24, 24.0f, looped: true, useKeyframes: true);
        long clipLengthTicks = animation.AuthoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency);

        animation.Start();
        animation.Tick(clipLengthTicks);

        animation.CurrentTime.ShouldBe(0.0f, 0.000001f);
    }

    [Test]
    public void SeekLong_WrapsNegativeTimeIntoPositiveClipRange()
    {
        var animation = new PropAnimFloat(24, 24.0f, looped: true, useKeyframes: true);
        long clipLengthTicks = animation.AuthoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency);
        long quarterTicks = clipLengthTicks / 4L;

        animation.Seek(-quarterTicks, wrapLooped: true);

        animation.CurrentTime.ShouldBe(0.75f, 0.0001f);
    }

    [Test]
    public void TickLong_ReversePlaybackClampsToStartWhenNotLooped()
    {
        var animation = new PropAnimFloat(24, 24.0f, looped: false, useKeyframes: true);
        long clipLengthTicks = animation.AuthoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency);

        animation.Seek(clipLengthTicks / 2L, wrapLooped: false);
        animation.Speed = -1.0f;
        animation.Start();
        animation.Tick(clipLengthTicks);

        animation.CurrentTime.ShouldBe(0.0f, 0.000001f);
        animation.State.ShouldBe(EAnimationState.Stopped);
    }

    [Test]
    public void ConstrainedKeyframedEvaluation_LoopedExactClipEndWrapsToFirstFrame()
    {
        var animation = new PropAnimFloat(24, 24.0f, looped: true, useKeyframes: true)
        {
            ConstrainKeyframedFPS = true,
            LerpConstrainedFPS = false,
        };

        animation.Keyframes.Add(
            new FloatKeyframe(0, 24.0f, 1.0f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(12, 24.0f, 2.0f, 0.0f, EVectorInterpType.Linear));

        animation.GetValueKeyframed(animation.LengthInSeconds).ShouldBe(animation.GetValueKeyframed(0.0f), 0.000001f);
    }

    [Test]
    public void BakedEvaluation_LoopedExactClipEndWrapsToFirstFrame()
    {
        var animation = new PropAnimFloat(24, 24.0f, looped: true, useKeyframes: true)
        {
            LerpConstrainedFPS = false,
        };

        animation.Keyframes.Add(
            new FloatKeyframe(0, 24.0f, 1.0f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(12, 24.0f, 2.0f, 0.0f, EVectorInterpType.Linear));

        animation.Bake(24);

        animation.GetValueBakedBySecond(animation.LengthInSeconds).ShouldBe(animation.GetValueBakedByFrame(0), 0.000001f);
    }

    [Test]
    public void TickLong_24FpsLoopedClip_RemainsDriftFreeAfterTenMinutesAt90Hz()
    {
        var animation = CreateFrameIndexedFloatAnimation(24);
        long clipLengthTicks = animation.AuthoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency);
        long displayDeltaTicks = (long)Math.Round(Stopwatch.Frequency / 90.0);
        long accumulatedTicks = 0L;

        animation.Start();

        for (int i = 0; i < 90 * 60 * 10; i++)
        {
            animation.Tick(displayDeltaTicks);
            accumulatedTicks = WrapStopwatchTicks(accumulatedTicks + displayDeltaTicks, clipLengthTicks);
        }

        animation.CurrentTime.ShouldBe((float)(accumulatedTicks / (double)Stopwatch.Frequency), 0.000001f);
    }

    [TestCase(24, 90)]
    [TestCase(30, 120)]
    [TestCase(60, 144)]
    public void ConstrainedKeyframedEvaluation_MixedRatePlayback_MapsDisplayTicksToExactFrames(int authoredFps, int displayHz)
    {
        var animation = CreateFrameIndexedFloatAnimation(authoredFps);
        long clipLengthTicks = animation.AuthoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency);
        long displayDeltaTicks = (long)Math.Round(Stopwatch.Frequency / (double)displayHz);
        long accumulatedTicks = 0L;

        animation.Start();

        for (int i = 0; i < displayHz; i++)
        {
            animation.Tick(displayDeltaTicks);
            accumulatedTicks = WrapStopwatchTicks(accumulatedTicks + displayDeltaTicks, clipLengthTicks);

            animation.CurrentTime.ShouldBe((float)(accumulatedTicks / (double)Stopwatch.Frequency), 0.000001f);

            int expectedFrame = animation.AuthoredCadence.GetFrameFloor(accumulatedTicks, Stopwatch.Frequency);
            int formulaFrame = Math.Clamp((int)(accumulatedTicks * (double)authoredFps / Stopwatch.Frequency), 0, authoredFps - 1);
            expectedFrame.ShouldBe(formulaFrame);
        }
    }

    [Test]
    public void SeekLong_ExactFrameBoundary_MapsToExpectedFrameWithoutOffByOne()
    {
        var animation = CreateFrameIndexedFloatAnimation(24);
        long frameTicks = (long)Math.Round(Stopwatch.Frequency / 24.0);

        animation.Seek(frameTicks * 12L, wrapLooped: false);
        animation.CurrentTime.ShouldBe(0.5f, 0.000001f);

        animation.Seek(animation.AuthoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency), wrapLooped: false);
        animation.CurrentTime.ShouldBe(1.0f, 0.000001f);
    }

    private static PropAnimFloat CreateFrameIndexedFloatAnimation(int fps)
    {
        var animation = new PropAnimFloat(fps, fps, looped: true, useKeyframes: true)
        {
            ConstrainKeyframedFPS = true,
            LerpConstrainedFPS = false,
        };

        for (int frame = 0; frame < fps; frame++)
            animation.Keyframes.Add(new FloatKeyframe(frame, fps, frame, 0.0f, EVectorInterpType.Step));

        return animation;
    }

    private static long WrapStopwatchTicks(long valueTicks, long lengthTicks)
    {
        if (lengthTicks <= 0L)
            return 0L;

        long wrappedTicks = valueTicks % lengthTicks;
        if (wrappedTicks < 0L)
            wrappedTicks += lengthTicks;
        return wrappedTicks;
    }
}