using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Data.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class KeyframeTrackLinkTests
{
    private const float FramesPerSecond = 60.0f;

    // Deep enough that a recursive walk over the list overflows the default thread stack.
    private const int LongTrackFrameCount = 100_000;

    [Test]
    public void LongTrack_LinksKeysInTimeOrder()
    {
        PropAnimFloat animation = CreateLongTrack(step: 1);

        animation.Keyframes.Count.ShouldBe(LongTrackFrameCount);
        AssertTimeOrder(animation, LongTrackFrameCount);
    }

    [Test]
    public void LongTrack_InsertsAKeyInTheMiddle()
    {
        PropAnimFloat animation = CreateLongTrack(step: 2);
        int middleFrame = LongTrackFrameCount / 2 + 1;

        // The first key starts the walk, so the search crosses half of the track.
        animation.Keyframes.Add(new FloatKeyframe(middleFrame, FramesPerSecond, -1.0f, 0.0f, EVectorInterpType.Linear));

        animation.Keyframes.Count.ShouldBe(LongTrackFrameCount / 2 + 1);
        AssertTimeOrder(animation, LongTrackFrameCount / 2 + 1);

        FloatKeyframe? inserted = null;
        for (Keyframe? key = animation.Keyframes.First; key is not null; key = key.Next)
        {
            if (key is FloatKeyframe floatKey && floatKey.AuthoredFrameIndex == middleFrame)
            {
                inserted = floatKey;
                break;
            }
        }

        inserted.ShouldNotBeNull();
        ((FloatKeyframe)inserted.Prev!).AuthoredFrameIndex.ShouldBe(middleFrame - 1);
        ((FloatKeyframe)inserted.Next!).AuthoredFrameIndex.ShouldBe(middleFrame + 1);
    }

    private static PropAnimFloat CreateLongTrack(int step)
    {
        PropAnimFloat animation = new(LongTrackFrameCount + 1, FramesPerSecond, looped: false, useKeyframes: true);
        for (int frame = 0; frame < LongTrackFrameCount; frame += step)
            animation.Keyframes.Add(new FloatKeyframe(frame, FramesPerSecond, frame, 0.0f, EVectorInterpType.Linear));
        return animation;
    }

    private static void AssertTimeOrder(PropAnimFloat animation, int expectedCount)
    {
        int count = 0;
        float previousSecond = float.NegativeInfinity;
        for (Keyframe? key = animation.Keyframes.First; key is not null; key = key.Next)
        {
            key.Second.ShouldBeGreaterThanOrEqualTo(previousSecond);
            key.OwningTrack.ShouldBeSameAs(animation.Keyframes);
            previousSecond = key.Second;
            ++count;
        }

        count.ShouldBe(expectedCount);
        animation.Keyframes.Last.ShouldNotBeNull();
        animation.Keyframes.Last!.IsLast.ShouldBeTrue();
    }
}
