using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class AnimationClipAuthoringActionsTests
{
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
