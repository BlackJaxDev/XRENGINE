using NUnit.Framework;
using System.Numerics;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class AnimationClipAuthoringActionsTests
{
    [Test]
    public void ReentrantSameClipActionIsRejectedBeforeItWrites()
    {
        Undo.ClearHistory();
        try
        {
            AnimationClip clip = new() { LengthInSeconds = 2 };
            bool? nested = null;
            clip.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(AnimationClip.LengthInSeconds) &&
                    clip.LengthInSeconds == 3)
                    nested = AnimationClipAuthoringActions.SetClipLength(clip, 4);
            };
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 3), Is.True);
            Assert.That(nested, Is.False);
            Assert.That(clip.LengthInSeconds, Is.EqualTo(3));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clip.LengthInSeconds, Is.EqualTo(2));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void DirectCallbackConflictDoesNotGetRolledBackOverNewerValue()
    {
        Undo.ClearHistory();
        try
        {
            AnimationClip clip = new() { LengthInSeconds = 2 };
            clip.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(AnimationClip.LengthInSeconds) &&
                    clip.LengthInSeconds == 3)
                    clip.LengthInSeconds = 4;
            };
            Undo.Track(clip);
            try
            {
                Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 3), Is.False);
                Assert.That(clip.LengthInSeconds, Is.EqualTo(4));
                Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
                Assert.That(clip.LengthInSeconds, Is.EqualTo(4));
            }
            finally { Undo.Untrack(clip); }
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void UnrelatedCallbackEditRemainsUndoableAfterExactRollback()
    {
        Undo.ClearHistory();
        try
        {
            AnimationClip clip = new() { LengthInSeconds = 2 };
            clip.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(AnimationClip.LengthInSeconds) ||
                    clip.LengthInSeconds != 3)
                    return;
                clip.Looped = true;
                throw new InvalidOperationException("observer failed");
            };
            Undo.Track(clip);
            try
            {
                Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 3), Is.False);
                Assert.That(clip.LengthInSeconds, Is.EqualTo(2));
                Assert.That(clip.Looped, Is.True);
                Assert.That(Undo.TryUndo(), Is.True);
                Assert.That(clip.Looped, Is.False);
            }
            finally { Undo.Untrack(clip); }
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void EarlierEqualTimeKeyRejectsRemovalBeforeChangingTrackOrHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member,
                0.5f, out Keyframe? first), Is.True);
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member,
                0.5f, out Keyframe? second), Is.True);
            Undo.ClearHistory();
            Assert.That(AnimationClipAuthoringActions.RemoveKey(clip, member,
                first!), Is.False);
            Assert.That(animation.Keyframes.Count, Is.EqualTo(2));
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(animation.Keyframes.Last, Is.SameAs(second));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void MemberTransferredDuringAddKeepsItsKeyAndGuardsOldHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationMember root = new("Root", EAnimationMemberType.Group,
                member);
            AnimationClip original = new(root) { LengthInSeconds = 2 };
            AnimationClip? replacement = null;
            animation.Keyframes.Changed += _ =>
            {
                if (replacement is not null)
                    return;
                root.Children.Remove(member);
                replacement = new AnimationClip(member) { LengthInSeconds = 2 };
            };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(original, member,
                0.5f, out _), Is.False);
            Assert.That(replacement?.RootMember, Is.SameAs(member));
            Assert.That(animation.Keyframes.Count, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(animation.Keyframes.Count, Is.EqualTo(1));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void FrameEditPreservesAuthoredIndexAcrossSubEpsilonTimeChange()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2,
                SampleRate = 30 };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip,
                member, 0.5f, out Keyframe? key), Is.True);
            key!.Second = 0.5000004f;
            key.AuthoredFrameIndex = 14;
            Undo.ClearHistory();
            Assert.That(AnimationClipAuthoringActions.SetKeyTime(clip, key,
                0.5f), Is.False);
            Assert.That(AnimationClipAuthoringActions.SetKeyFrame(clip, key,
                15), Is.True);
            Assert.That(key.AuthoredFrameIndex, Is.EqualTo(15));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.Second, Is.EqualTo(0.5000004f));
            Assert.That(key.AuthoredFrameIndex, Is.EqualTo(14));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ClipSettingsUseUndoAndRejectInvalidValues()
    {
        Undo.ClearHistory();
        try
        {
            AnimationClip clip = new() { LengthInSeconds = 2,
                SampleRate = 30, Looped = false };
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, float.NaN),
                Is.False);
            Assert.That(AnimationClipAuthoringActions.SetClipSampleRate(clip, 0),
                Is.False);
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 3), Is.True);
            Assert.That(AnimationClipAuthoringActions.SetClipSampleRate(clip, 60),
                Is.True);
            Assert.That(AnimationClipAuthoringActions.SetClipLooped(clip, true),
                Is.True);
            Assert.That((clip.LengthInSeconds, clip.SampleRate, clip.Looped),
                Is.EqualTo((3f, 60, true)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clip.Looped, Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clip.SampleRate, Is.EqualTo(30));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clip.LengthInSeconds, Is.EqualTo(2f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void FrameTangentAndInterpolationRequireCurrentKeyAndUndoExactly()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2,
                SampleRate = 30 };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip,
                member, 0.5f, out Keyframe? created), Is.True);
            FloatKeyframe key = (FloatKeyframe)created!;
            Undo.ClearHistory();
            Assert.That(AnimationClipAuthoringActions.SetKeyFrame(clip, key, 45),
                Is.True);
            Assert.That((key.Second, key.AuthoredFrameIndex),
                Is.EqualTo((1.5f, 45)));
            Assert.That(AnimationClipAuthoringActions.SetKeyTangent(clip, key,
                outgoing: true, 2f), Is.True);
            Assert.That(key.OutTangent, Is.EqualTo(2f));
            Assert.That(AnimationClipAuthoringActions.SetKeyInterpolation(clip,
                key, outgoing: true, EVectorInterpType.Hermite), Is.True);
            Assert.That(key.InterpolationTypeOut,
                Is.EqualTo(EVectorInterpType.Hermite));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.InterpolationTypeOut,
                Is.EqualTo(EVectorInterpType.Linear));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.OutTangent, Is.EqualTo(0f));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((key.Second, key.AuthoredFrameIndex),
                Is.EqualTo((0.5f, -1)));

            AnimationClip foreign = new();
            Assert.That(AnimationClipAuthoringActions.SetKeyFrame(foreign, key, 30),
                Is.False);
            Assert.That(AnimationClipAuthoringActions.SetKeyTangent(foreign, key,
                outgoing: true, 4f), Is.False);
            Assert.That(AnimationClipAuthoringActions.SetKeyInterpolation(foreign,
                key, outgoing: true, EVectorInterpType.Step), Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void QuaternionTangentAndInterpolationUseTypedAuthoring()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimQuaternion animation = new(2, false, true);
            AnimationMember member = new("Rotation", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip,
                member, 0.25f, out Keyframe? created), Is.True);
            QuaternionKeyframe key = (QuaternionKeyframe)created!;
            Undo.ClearHistory();
            Quaternion tangent = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
            Assert.That(AnimationClipAuthoringActions.SetKeyTangent(clip, key,
                outgoing: false, tangent), Is.True);
            Assert.That(AnimationClipAuthoringActions.SetKeyInterpolation(clip, key,
                outgoing: false, ERadialInterpType.Linear), Is.True);
            Assert.That(key.InTangent, Is.EqualTo(tangent));
            Assert.That(key.InterpolationTypeIn,
                Is.EqualTo(ERadialInterpType.Linear));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.InterpolationTypeIn,
                Is.EqualTo(ERadialInterpType.Step));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.InTangent, Is.EqualTo(Quaternion.Identity));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ThrowingRemoveCallbackRestoresTwoKeyTrackAndCancelsUndo()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member, 0.25f, out Keyframe? first), Is.True);
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member, 1.25f, out Keyframe? second), Is.True);
            Undo.ClearHistory();
            animation.Keyframes.Changed += Throw;

            Assert.That(AnimationClipAuthoringActions.RemoveKey(clip, member, second!), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(animation.Keyframes.Count, Is.EqualTo(2));
                Assert.That(animation.Keyframes.First, Is.SameAs(first));
                Assert.That(animation.Keyframes.Last, Is.SameAs(second));
                Assert.That(first!.Next, Is.SameAs(second));
                Assert.That(second!.Prev, Is.SameAs(first));
                Assert.That(second.OwningTrack, Is.SameAs(animation.Keyframes));
                Assert.That(Undo.CanUndo, Is.False);
            });
        }
        finally
        {
            Undo.ClearHistory();
        }

        static void Throw(BaseKeyframeTrack _) => throw new InvalidOperationException("Changed callback failed");
    }

    [Test]
    public void ThrowingTrackCallbackRestoresKeyTimeWithoutLosingAnUndoInverse()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member, 0.25f, out Keyframe? key), Is.True);
            Assert.That(key, Is.Not.Null);
            Undo.ClearHistory();
            key!.OwningTrack!.Changed += Throw;

            Assert.That(AnimationClipAuthoringActions.SetKeyTime(clip, key, 1.25f), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(key.Second, Is.EqualTo(0.25f).Within(0.000001f));
                Assert.That(AnimationClipAuthoringActions.ContainsKey(clip, key), Is.True);
                Assert.That(Undo.CanUndo, Is.False);
            });
        }
        finally
        {
            Undo.ClearHistory();
        }

        static void Throw(BaseKeyframeTrack _) => throw new InvalidOperationException("Changed callback failed");
    }

    [Test]
    public void RemovedMemberIsRejectedAsAnAuthoringTargetUntilUndoRestoresIt()
    {
        Undo.ClearHistory();
        try
        {
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                new PropAnimFloat(2, false, true));
            AnimationMember root = new("Root", EAnimationMemberType.Group, member);
            AnimationClip clip = new(root) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.RemoveMember(clip, member), Is.True);
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member, 0.5f, out _), Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(AnimationClipAuthoringActions.ContainsMember(clip, member), Is.True);
            Assert.That(AnimationClipAuthoringActions.TryAddKey(clip, member, 0.5f, out _), Is.True);
        }
        finally
        {
            Undo.ClearHistory();
        }
    }
}
