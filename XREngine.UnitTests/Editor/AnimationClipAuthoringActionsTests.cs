using NUnit.Framework;
using System.Numerics;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class AnimationClipAuthoringActionsTests
{
    [Test]
    public void ConsecutiveLengthBatchesUndoAndRedoAllUniqueChildren()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(1.0f, false, true);
            FloatKeyframe key = new(0.5f, 2f, 0f, EVectorInterpType.Linear);
            animation.Keyframes.Add(key);
            AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group,
                new AnimationMember("First", EAnimationMemberType.Property, animation),
                new AnimationMember("Alias", EAnimationMemberType.Property, animation))) { LengthInSeconds = 2 };
            int notices = 0;
            animation.LengthChanged += _ => notices++;
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 2, true), Is.True);
            Assert.That((clip.LengthInSeconds, animation.LengthInSeconds, key.Second), Is.EqualTo((2f, 2f, 1f)));
            Assert.That(notices, Is.EqualTo(1));
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 4, true), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((clip.LengthInSeconds, animation.LengthInSeconds, key.Second), Is.EqualTo((2f, 1f, 0.5f)));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((clip.LengthInSeconds, animation.LengthInSeconds, key.Second), Is.EqualTo((4f, 4f, 2f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LengthObserverSeesWholeBatchAndThrowRestoresIt()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(1.0f, false, true);
            PropAnimFloat second = new(2.0f, false, true);
            AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group,
                new AnimationMember("First", EAnimationMemberType.Property, first),
                new AnimationMember("Second", EAnimationMemberType.Property, second))) { LengthInSeconds = 2 };
            bool once = true;
            clip.PropertyChanged += (_, change) =>
            {
                if (!once || change.PropertyName != nameof(AnimationClip.LengthInSeconds)) return;
                once = false;
                Assert.That((first.LengthInSeconds, first.Keyframes.LengthInSeconds,
                    second.LengthInSeconds, second.Keyframes.LengthInSeconds), Is.EqualTo((4f, 4f, 4f, 4f)));
                throw new InvalidOperationException("observer failed");
            };
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 4), Is.False);
            Assert.That((clip.LengthInSeconds, first.LengthInSeconds, second.LengthInSeconds), Is.EqualTo((2f, 1f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LengthBatchRejectsUnsupportedCadenceBeforeClipWrite()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(48, 24f, false, true);
            AnimationClip clip = new(new AnimationMember("Value", EAnimationMemberType.Property, animation)) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 4), Is.False);
            Assert.That((clip.LengthInSeconds, animation.LengthInSeconds), Is.EqualTo((2f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LoopEditUpdatesUniqueChildAnimationsAndRestoresMixedPriorValues()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat firstAnimation = new(2, false, true);
            PropAnimFloat secondAnimation = new(2, true, true);
            AnimationMember first = new("First", EAnimationMemberType.Property,
                firstAnimation);
            AnimationMember alias = new("Alias", EAnimationMemberType.Property,
                firstAnimation);
            AnimationMember second = new("Second", EAnimationMemberType.Property,
                secondAnimation);
            AnimationClip clip = new(new AnimationMember("Root",
                EAnimationMemberType.Group, first, alias, second))
            { Looped = false };
            int firstNotices = 0;
            int secondNotices = 0;
            firstAnimation.LoopChanged += _ => firstNotices++;
            secondAnimation.LoopChanged += _ => secondNotices++;
            Assert.That(AnimationClipAuthoringActions.SetClipLooped(clip,
                true), Is.True);
            Assert.That((clip.Looped, firstAnimation.Looped,
                secondAnimation.Looped), Is.EqualTo((true, true, true)));
            Assert.That((firstNotices, secondNotices), Is.EqualTo((1, 1)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((clip.Looped, firstAnimation.Looped,
                secondAnimation.Looped), Is.EqualTo((false, false, true)));
            Assert.That((firstNotices, secondNotices), Is.EqualTo((2, 2)));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((clip.Looped, firstAnimation.Looped,
                secondAnimation.Looped), Is.EqualTo((true, true, true)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LoopPrechangeTreeReplacementRejectsBatchBeforeAnyLoopWrite()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationMember root = new("Root", EAnimationMemberType.Group,
                member);
            AnimationClip clip = new(root) { Looped = false };
            AnimationMember replacement = new("Other");
            clip.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(AnimationClip.Looped))
                {
                    root.Children.Remove(member);
                    root.Children.Add(replacement);
                }
            };
            Assert.That(AnimationClipAuthoringActions.SetClipLooped(clip,
                true), Is.False);
            Assert.That(clip.Looped, Is.False);
            Assert.That(animation.Looped, Is.False);
            Assert.That(root.Children[0], Is.SameAs(replacement));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LoopPrechangeVetoLeavesClipChildrenAndHistoryUntouched()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationClip clip = new(new AnimationMember("Value",
                EAnimationMemberType.Property, animation)) { Looped = false };
            clip.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(AnimationClip.Looped) &&
                    change is XRPropertyChangingEventArgs<bool> loopChange)
                    loopChange.AllowChange = false;
            };
            Assert.That(AnimationClipAuthoringActions.SetClipLooped(clip,
                true), Is.False);
            Assert.That((clip.Looped, animation.Looped),
                Is.EqualTo((false, false)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LaterLoopCallbackCannotOverwriteEarlierChildConflict()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat firstAnimation = new(2, false, true);
            PropAnimFloat secondAnimation = new(2, false, true);
            AnimationMember root = new("Root", EAnimationMemberType.Group,
                new AnimationMember("First", EAnimationMemberType.Property,
                    firstAnimation),
                new AnimationMember("Second", EAnimationMemberType.Property,
                    secondAnimation));
            AnimationClip clip = new(root) { Looped = false };
            secondAnimation.LoopChanged += _ => firstAnimation.Looped = false;
            Assert.That(AnimationClipAuthoringActions.SetClipLooped(clip,
                true), Is.False);
            Assert.That((clip.Looped, firstAnimation.Looped,
                secondAnimation.Looped), Is.EqualTo((true, false, true)));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(firstAnimation.Looped, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ThrowAfterCompleteLoopCommitRestoresEveryChild()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat firstAnimation = new(2, false, true);
            PropAnimFloat secondAnimation = new(2, false, true);
            AnimationClip clip = new(new AnimationMember("Root",
                EAnimationMemberType.Group,
                new AnimationMember("First", EAnimationMemberType.Property,
                    firstAnimation),
                new AnimationMember("Second", EAnimationMemberType.Property,
                    secondAnimation))) { Looped = false };
            bool throwOnce = true;
            clip.PropertyChanged += (_, change) =>
            {
                if (!throwOnce || change.PropertyName != nameof(AnimationClip.Looped))
                    return;
                throwOnce = false;
                throw new InvalidOperationException("loop observer failed");
            };
            Assert.That(AnimationClipAuthoringActions.SetClipLooped(clip,
                true), Is.False);
            Assert.That((clip.Looped, firstAnimation.Looped,
                secondAnimation.Looped), Is.EqualTo((false, false, false)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void TraversalAndKindEditsUseExactUndoHistory()
    {
        Undo.ClearHistory();
        try
        {
            AnimationClip clip = new();
            EAnimTreeTraversalMethod originalTraversal = clip.TraversalMethod;
            EAnimationClipKind originalKind = clip.ClipKind;
            Assert.That(AnimationClipAuthoringActions.SetClipTraversalMethod(clip,
                EAnimTreeTraversalMethod.Parallel), Is.True);
            Assert.That(AnimationClipAuthoringActions.SetClipKind(clip,
                EAnimationClipKind.GenericTransform), Is.True);
            Assert.That(AnimationClipAuthoringActions.SetClipKind(clip,
                (EAnimationClipKind)int.MaxValue), Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clip.ClipKind, Is.EqualTo(originalKind));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clip.TraversalMethod, Is.EqualTo(originalTraversal));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(clip.TraversalMethod, Is.EqualTo(EAnimTreeTraversalMethod.Parallel));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(clip.ClipKind, Is.EqualTo(EAnimationClipKind.GenericTransform));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void IncomingValueAndUnifyKeepCompletePlanarStateInHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            FloatKeyframe key = new(0.5f, 1f, 3f, 2f, 4f,
                EVectorInterpType.Linear);
            key.RestorePublishedHandles(1f, 3f, 2f, 4f,
                EVectorInterpType.Linear, EVectorInterpType.Linear,
                false, false, false);
            animation.Keyframes.Add(key);
            Assert.That(AnimationClipAuthoringActions.SetKeyIncomingValue(clip,
                key, 5f), Is.True);
            Assert.That((key.InValue, key.OutValue), Is.EqualTo((5f, 3f)));
            Assert.That(AnimationClipAuthoringActions.UnifyKey(clip, key), Is.True);
            Assert.That(key.InValue, Is.EqualTo(key.OutValue));
            Assert.That(key.InTangent, Is.EqualTo(-key.OutTangent));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((key.InValue, key.OutValue, key.InTangent, key.OutTangent),
                Is.EqualTo((5f, 3f, 2f, 4f)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((key.InValue, key.OutValue), Is.EqualTo((1f, 3f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void QuaternionIncomingValueKeepsOutgoingValueAndRestoresOnUndo()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimQuaternion animation = new(2, false, true);
            AnimationMember member = new("Rotation", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            QuaternionKeyframe key = new(0.5f, Quaternion.Identity,
                Quaternion.Identity, Quaternion.Identity,
                ERadialInterpType.Smooth);
            animation.Keyframes.Add(key);
            Quaternion next = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
            Assert.That(AnimationClipAuthoringActions.SetKeyIncomingValue(clip,
                key, next), Is.True);
            Assert.That(key.InValue, Is.EqualTo(next));
            Assert.That(key.OutValue, Is.EqualTo(Quaternion.Identity));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.InValue, Is.EqualTo(Quaternion.Identity));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void AutoTangentsUseExactNeighborValuesAndUndoBothHandles()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            FloatKeyframe first = new(0f, 0f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe middle = new(1f, 1f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe last = new(2f, 3f, 0f,
                EVectorInterpType.Linear);
            middle.RestorePublishedHandles(1f, 1f, 0f, 0f,
                EVectorInterpType.Linear, EVectorInterpType.Linear,
                false, false, false);
            animation.Keyframes.Add(first);
            animation.Keyframes.Add(middle);
            animation.Keyframes.Add(last);
            Assert.That(AnimationClipAuthoringActions.AutoKeyTangents(clip,
                middle), Is.True);
            Assert.That((middle.InTangent, middle.OutTangent),
                Is.EqualTo((-1f, 2f)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((middle.InTangent, middle.OutTangent),
                Is.EqualTo((0f, 0f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void AutoTangentCallbackNeighborEditIsKeptAndHistoryIsGuarded()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            FloatKeyframe first = new(0f, 0f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe middle = new(1f, 1f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe last = new(2f, 3f, 0f,
                EVectorInterpType.Linear);
            middle.RestorePublishedHandles(1f, 1f, 0f, 0f,
                EVectorInterpType.Linear, EVectorInterpType.Linear,
                false, false, false);
            animation.Keyframes.Add(first);
            animation.Keyframes.Add(middle);
            animation.Keyframes.Add(last);
            bool changed = false;
            animation.Keyframes.Changed += _ =>
            {
                if (changed || middle.OutTangent != 2f)
                    return;
                changed = true;
                first.OutValue = 9f;
            };
            Assert.That(AnimationClipAuthoringActions.AutoKeyTangents(clip,
                middle), Is.False);
            Assert.That(first.OutValue, Is.EqualTo(9f));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(first.OutValue, Is.EqualTo(9f));
        }
        finally { Undo.ClearHistory(); }
    }

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
    public void EarlierEqualTimeKeyRestoresExactOrderThroughUndoAndRedo()
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
                first!), Is.True);
            Assert.That(animation.Keyframes.Count, Is.EqualTo(1));
            Assert.That(animation.Keyframes.First, Is.SameAs(second));
            Assert.That(first!.OwningTrack, Is.Null);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(animation.Keyframes.Last, Is.SameAs(second));
            Assert.That(first.Next, Is.SameAs(second));
            Assert.That(second!.Prev, Is.SameAs(first));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(animation.Keyframes.First, Is.SameAs(second));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void RemovedCachedKeyDoesNotControlPlaybackAfterEditOrReplay()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2 };
            FloatKeyframe first = new(0f, 0f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe middle = new(1f, 100f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe last = new(2f, 0f, 0f,
                EVectorInterpType.Linear);
            animation.Keyframes.Add(first);
            animation.Keyframes.Add(middle);
            animation.Keyframes.Add(last);
            animation.Seek(1f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(100f));
            Assert.That(AnimationClipAuthoringActions.RemoveKey(clip,
                member, middle), Is.True);
            animation.Seek(1f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(0f));
            Assert.That(Undo.TryUndo(), Is.True);
            animation.Seek(1f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(100f));
            Assert.That(Undo.TryRedo(), Is.True);
            animation.Seek(1f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(0f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void EarlierTieTimeEditRestoresOriginalTiePosition()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2,
                SampleRate = 2 };
            FloatKeyframe first = new(0.5f, 1f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe second = new(0.5f, 2f, 0f,
                EVectorInterpType.Linear);
            animation.Keyframes.Add(first);
            animation.Keyframes.Add(second);
            animation.Seek(0.5f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(2f));
            Assert.That(AnimationClipAuthoringActions.SetKeyFrame(clip,
                first, 1), Is.True);
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(first.Next, Is.SameAs(second));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(AnimationClipAuthoringActions.SetKeyTime(clip,
                first, 1f), Is.True);
            Assert.That(animation.Keyframes.First, Is.SameAs(second));
            animation.Seek(0.5f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(2f));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(first.Next, Is.SameAs(second));
            animation.Seek(0.5f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.EqualTo(2f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void SubEpsilonFrameEditReordersNearTieAndRestoresIt()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, true);
            AnimationMember member = new("Value", EAnimationMemberType.Property,
                animation);
            AnimationClip clip = new(member) { LengthInSeconds = 2,
                SampleRate = 4 };
            FloatKeyframe first = new(0.24999995f, 1f, 0f,
                EVectorInterpType.Linear);
            FloatKeyframe second = new(0.24999995f, 2f, 0f,
                EVectorInterpType.Linear);
            animation.Keyframes.Add(first);
            animation.Keyframes.Add(second);
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(AnimationClipAuthoringActions.SetKeyFrame(clip,
                first, 1), Is.True);
            Assert.That(first.Second, Is.EqualTo(0.25f));
            Assert.That(animation.Keyframes.First, Is.SameAs(second));
            Assert.That(animation.Keyframes.Last, Is.SameAs(first));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.Second, Is.EqualTo(0.24999995f));
            Assert.That(animation.Keyframes.First, Is.SameAs(first));
            Assert.That(animation.Keyframes.Last, Is.SameAs(second));
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
