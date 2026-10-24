using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using System.Numerics;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class AnimationLengthEditTests
{
    [Test]
    public void QuaternionPlaybackMatchesFreshSamplingAfterStretchAndUndo()
    {
        PropAnimQuaternion animation = new(2.0f, false, true);
        QuaternionKeyframe first = new(0f, Quaternion.Identity, Quaternion.Identity, ERadialInterpType.Linear);
        QuaternionKeyframe last = new(1f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.5f), Quaternion.Identity, ERadialInterpType.Linear);
        animation.Keyframes.Add(first, last);
        animation.Seek(0.8f, false);
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes,
            [first, last], [static () => true, static () => true], 4, true,
            out var edit, out string reason), Is.True, reason);
        edit!.Commit(true);
        animation.Seek(0.8f, false);
        Assert.That(MathF.Abs(Quaternion.Dot(animation.CurrentValue,
            animation.GetValueKeyframed(0.8f))), Is.EqualTo(1f).Within(0.00001f));
        edit.Commit(false);
        animation.Seek(0.8f, false);
        Assert.That(MathF.Abs(Quaternion.Dot(animation.CurrentValue,
            animation.GetValueKeyframed(0.8f))), Is.EqualTo(1f).Within(0.00001f));
    }

    [Test]
    public void StretchedTimeOverflowRejectsBeforeAnyWrite()
    {
        PropAnimFloat animation = new(float.Epsilon, false, true);
        FloatKeyframe key = new(1f, 2f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(key);
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes,
            [key], [static () => true], 3600, true, out _, out _), Is.False);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(float.Epsilon));
        Assert.That(key.Second, Is.EqualTo(1));
    }

    [Test]
    public void ConsecutiveEditsCanRestoreEarlierSnapshotAfterGenerationAdvances()
    {
        PropAnimFloat animation = new(2.0f, false, true);
        AnimationLengthEdit first = Capture(animation, 3, false);
        first.Commit(true);
        AnimationLengthEdit second = Capture(animation, 4, false);
        second.Commit(true);
        second.Commit(false);
        Assert.That(first.CanCommit(false), Is.True);
        first.Commit(false);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(2));
        first.Commit(true);
        Assert.That(second.CanCommit(true), Is.True);
        second.Commit(true);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(4));
    }

    private static AnimationLengthEdit Capture(PropAnimFloat animation, float seconds, bool stretch)
    {
        Keyframe[] keys = animation.Keyframes.Cast<Keyframe>().ToArray();
        Func<bool>[] payloads = keys.Select(key =>
        {
            float value = ((FloatKeyframe)key).OutValue;
            return (Func<bool>)(() => ((FloatKeyframe)key).OutValue == value);
        }).ToArray();
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes, keys,
            payloads, seconds, stretch, out var edit, out string reason), Is.True, reason);
        return edit!;
    }

    [Test]
    public void StretchCommitsCompleteTimingAndRestoresExactFramesWithoutChangingPlayback()
    {
        PropAnimFloat animation = new(2.0f, true, true);
        FloatKeyframe first = new(6, 24f, 1f, 0f, EVectorInterpType.Linear);
        FloatKeyframe second = new(24, 24f, 2f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(first, second);
        animation.Seek(0.5f, false);
        AnimationLengthEdit edit = Capture(animation, 4, true);
        int notices = 0;
        animation.LengthChanged += _ =>
        {
            notices++;
            Assert.That(animation.Keyframes.LengthInSeconds, Is.EqualTo(animation.LengthInSeconds));
            Assert.That(second.Second, Is.EqualTo(animation.LengthInSeconds / 2));
        };
        for (int round = 0; round < 3; round++)
        {
            Assert.That(edit.CanCommit(true), Is.True);
            edit.Commit(true);
            edit.Publish(true, () => Assert.That(edit.Matches(true), Is.True));
            Assert.That((first.Second, second.Second), Is.EqualTo((0.5f, 2f)));
            Assert.That((first.AuthoredFrameIndex, second.AuthoredFrameIndex), Is.EqualTo((-1, -1)));
            Assert.That(animation.CurrentTime, Is.EqualTo(0.5f));
            Assert.That(edit.CanCommit(false), Is.True);
            edit.Commit(false);
            edit.Publish(false, () => Assert.That(edit.Matches(false), Is.True));
            Assert.That((first.Second, second.Second), Is.EqualTo((0.25f, 1f)));
            Assert.That((first.AuthoredFrameIndex, second.AuthoredFrameIndex), Is.EqualTo((6, 24)));
        }
        Assert.That(notices, Is.EqualTo(6));
    }

    [Test]
    public void NonStretchPreservesKeysBeyondNewEnd()
    {
        PropAnimFloat animation = new(2.0f, false, true);
        FloatKeyframe key = new(1.5f, 7f, 3f, EVectorInterpType.Smooth);
        animation.Keyframes.Add(key);
        AnimationLengthEdit edit = Capture(animation, 0.5f, false);
        edit.Commit(true);
        Assert.That(edit.Matches(true), Is.True);
        Assert.That((key.Second, key.OutValue, key.OutTangent), Is.EqualTo((1.5f, 7f, 3f)));
    }

    [Test]
    public void OrdinaryTrackEditInsideExactNotificationStillUpdatesAnimation()
    {
        PropAnimFloat animation = new(2.0f, false, true);
        AnimationLengthEdit edit = Capture(animation, 4, false);
        bool once = true;
        animation.Keyframes.LengthChanged += (_, track) =>
        {
            if (!once) return;
            once = false;
            track.LengthInSeconds = 7;
        };
        edit.Commit(true);
        Assert.Throws<InvalidOperationException>(() => edit.Publish(true, () =>
        {
            if (!edit.Matches(true)) throw new InvalidOperationException("conflict");
        }));
        Assert.That((animation.LengthInSeconds, animation.Keyframes.LengthInSeconds), Is.EqualTo((7f, 7f)));
        Assert.That(edit.CanCommit(false), Is.False);
        animation.Keyframes.LengthInSeconds = 8;
        Assert.That(animation.LengthInSeconds, Is.EqualTo(8));
    }

    [Test]
    public void ReplacedTrackOrChangedPayloadRejectsReplay()
    {
        PropAnimFloat animation = new(2.0f, false, true);
        FloatKeyframe key = new(1f, 7f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(key);
        AnimationLengthEdit edit = Capture(animation, 4, false);
        edit.Commit(true);
        key.OutValue = 9;
        Assert.That(edit.CanCommit(false), Is.False);
        key.OutValue = 7;
        Assert.That(edit.CanCommit(false), Is.True);
        animation.Keyframes = new KeyframeTrack<FloatKeyframe>();
        Assert.That(edit.CanCommit(false), Is.False);
    }

    [Test]
    public void SharedTrackWithAnimationOutsideClipRejectsBeforeWrite()
    {
        PropAnimFloat animation = new(2.0f, false, true);
        PropAnimFloat outside = new(2.0f, false, true) { Keyframes = animation.Keyframes };
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes, [], [],
            4, false, out _, out _), Is.False);
        Assert.That((animation.LengthInSeconds, outside.LengthInSeconds), Is.EqualTo((2f, 2f)));
    }

    [Test]
    public void AuthoredCadenceRemainsExactAcrossLengthEdit()
    {
        PropAnimFloat animation = new(48, 24.0f, false, true);
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes, [], [],
            4, false, out var edit, out string reason), Is.True, reason);
        edit!.Commit(true);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(4));
        Assert.That(animation.AuthoredFrameCount, Is.EqualTo(48));
        edit.Commit(false);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(2));
    }

    [Test]
    public void BakedAnimationPreservesStoredSamplesDuringLengthEdit()
    {
        PropAnimFloat animation = new(2.0f, false, false);
        Assert.That(animation.IsBaked, Is.True);
        object? store = animation.ExactBakedStore;
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes, [], [],
            4, false, out var edit, out string reason), Is.True, reason);
        edit!.Commit(true);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(4));
        Assert.That(animation.ExactBakedStore, Is.SameAs(store));
        edit.Commit(false);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(2));
    }

    [Test]
    public void ZeroLengthStretchKeepsStableKeyOrderAndCanUndo()
    {
        PropAnimFloat animation = new(2.0f, false, true);
        FloatKeyframe first = new(0.5f, 1f, 0f, EVectorInterpType.Linear);
        FloatKeyframe second = new(1.5f, 2f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(first, second);
        AnimationLengthEdit edit = Capture(animation, 0, true);
        edit.Commit(true);
        Assert.That(animation.Keyframes.First, Is.SameAs(first));
        Assert.That(first.Next, Is.SameAs(second));
        Assert.That((first.Second, second.Second), Is.EqualTo((0f, 0f)));
        edit.Commit(false);
        Assert.That((first.Second, second.Second), Is.EqualTo((0.5f, 1.5f)));
    }
}
