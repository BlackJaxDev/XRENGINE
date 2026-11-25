using NUnit.Framework;
using XREngine.Animation;
using XREngine.Data.Animation;
using XREngine.Data.Core;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class AnimationLengthAdapterTests
{
    [Test]
    public void BakedLengthUndoRedoPreservesEncodedSamplesAndMode()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(2, false, false);
            animation.BakedValueCompressionAlgorithm = EAnimationValueCompressionAlgorithm.Constant;
            AnimationClip clip = new(new AnimationMember("Value",
                EAnimationMemberType.Property, animation)) { LengthInSeconds = 2 };
            object? store = animation.ExactBakedStore;
            float[] samples = animation.CapturePublishedBakedValues();
            EAnimationValueCompressionAlgorithm encoded =
                animation.EncodedBakedValueCompressionAlgorithm;
            int notices = 0;
            animation.LengthChanged += _ => notices++;
            Assert.That(store, Is.Not.Null);
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 3), Is.True);
            Assert.That((clip.LengthInSeconds, animation.LengthInSeconds,
                animation.BakedFrameCount, animation.IsBaked), Is.EqualTo((3f, 3f, 180, true)));
            Assert.That(animation.ExactBakedStore, Is.SameAs(store));
            Assert.That(animation.EncodedBakedValueCompressionAlgorithm, Is.EqualTo(encoded));
            Assert.That(animation.CapturePublishedBakedValues(), Is.EqualTo(samples));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((animation.LengthInSeconds, animation.BakedFrameCount), Is.EqualTo((2f, 120)));
            Assert.That(animation.ExactBakedStore, Is.SameAs(store));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(animation.ExactBakedStore, Is.SameAs(store));
            Assert.That(notices, Is.EqualTo(3));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void AuthoredCadenceLengthKeepsFrameCadenceAndPlaybackBoundary()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(48, 24f, false, true);
            AnimationClip clip = new(new AnimationMember("Value",
                EAnimationMemberType.Property, animation)) { LengthInSeconds = 2 };
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 4), Is.True);
            Assert.That((animation.AuthoredFrameCount, animation.AuthoredFramesPerSecond),
                Is.EqualTo((48, 24)));
            Assert.That(animation.LengthInSeconds, Is.EqualTo(4));
            animation.Start();
            animation.Tick(2.5f);
            Assert.That(animation.State, Is.EqualTo(EAnimationState.Stopped));
            animation.Seek(3, wrapLooped: false);
            Assert.That(animation.CurrentTime, Is.EqualTo(4));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((animation.AuthoredFrameCount, animation.AuthoredFramesPerSecond),
                Is.EqualTo((48, 24)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ChangedBakedStoreOrCadenceInvalidatesExactReceipt()
    {
        PropAnimFloat baked = new(2, false, false);
        Assert.That(AnimationLengthEdit.TryCapture(baked, baked.Keyframes, [], [],
            3, false, out AnimationLengthEdit? edit, out string reason), Is.True, reason);
        baked.Bake(baked.BakedFramesPerSecond);
        Assert.That(edit!.CanCommit(true), Is.False);

        PropAnimFloat mode = new(2, false, false);
        Assert.That(AnimationLengthEdit.TryCapture(mode, mode.Keyframes, [], [],
            3, false, out edit, out reason), Is.True, reason);
        mode.IsBaked = false;
        Assert.That(edit!.CanCommit(true), Is.False);

        PropAnimFloat sampling = new(2, false, false);
        Assert.That(AnimationLengthEdit.TryCapture(sampling, sampling.Keyframes,
            [], [], 3, false, out edit, out reason), Is.True, reason);
        sampling.LerpConstrainedFPS = true;
        Assert.That(edit!.CanCommit(true), Is.False);

        PropAnimFloat cadence = new(48, 24f, false, true);
        Assert.That(AnimationLengthEdit.TryCapture(cadence, cadence.Keyframes, [], [],
            3, false, out edit, out reason), Is.True, reason);
        cadence.AuthoredFrameCount = 72;
        Assert.That(edit!.CanCommit(true), Is.False);
    }

    [Test]
    public void RebakeDuringTrackLengthObserverStopsLaterLengthNotification()
    {
        PropAnimFloat animation = new(2, false, false);
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes,
            [], [], 3, false, out AnimationLengthEdit? edit,
            out string reason), Is.True, reason);
        int lengthNotices = 0;
        animation.LengthChanged += _ => lengthNotices++;
        animation.Keyframes.LengthChanged += (_, _) => animation.Bake(30);
        edit!.Commit(true);
        Assert.Throws<InvalidOperationException>(() => edit.Publish(true, () =>
        {
            if (!edit.Matches(true))
                throw new InvalidOperationException("Baked payload changed during notification.");
        }));
        Assert.That(lengthNotices, Is.Zero);
        Assert.That(edit.CanCommit(false), Is.False);
    }

    [Test]
    public void StretchedCadenceEditInvalidatesWarmKeySearchCacheAndUndoRestoresIt()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat animation = new(48, 24f, false, true);
            animation.Keyframes.Add(
                new FloatKeyframe(0, 0, 0, EVectorInterpType.Linear),
                new FloatKeyframe(1, 10, 0, EVectorInterpType.Linear));
            AnimationClip clip = new(new AnimationMember("Value",
                EAnimationMemberType.Property, animation)) { LengthInSeconds = 2 };
            Assert.That(animation.GetValueKeyframed(1), Is.EqualTo(10));
            Assert.That(AnimationClipAuthoringActions.SetClipLength(clip, 4, stretch: true), Is.True);
            Assert.That(animation.GetValueKeyframed(1), Is.EqualTo(5).Within(0.001));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(animation.GetValueKeyframed(1), Is.EqualTo(10));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LerpedBakedExpansionRejectsOutOfRangeSampleWindow()
    {
        PropAnimFloat animation = new(2, false, false) { LerpConstrainedFPS = true };
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes,
            [], [], 4, false, out _, out _), Is.False);
        Assert.That(animation.LengthInSeconds, Is.EqualTo(2));
        Assert.That(animation.BakedFrameCount, Is.EqualTo(120));
    }

    [Test]
    public void LerpedBakedCaptureRejectsUndercountedBeforeState()
    {
        PropAnimFloat animation = new(2, false, false);
        animation.Bake(30);
        animation.RestorePublishedBakedState(60, 1,
            animation.BakedValueCompressionAlgorithm,
            animation.EncodedBakedValueCompressionAlgorithm, isBaked: true);
        animation.LerpConstrainedFPS = true;
        Assert.That(animation.ExactBakedStoreCount, Is.EqualTo(60));
        Assert.That(AnimationLengthEdit.TryCapture(animation, animation.Keyframes,
            [], [], 1, false, out _, out _), Is.False);
    }
}
