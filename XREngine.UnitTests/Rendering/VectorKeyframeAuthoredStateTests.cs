using NUnit.Framework;
using XREngine.Animation;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VectorKeyframeAuthoredStateTests
{
    [Test]
    public void ExactInterpolationEditChangesSamplingAndRestoresHandles()
    {
        PropAnimFloat animation = new(2, false, true);
        FloatKeyframe first = new(0f, 0f, 0f, EVectorInterpType.Linear);
        FloatKeyframe second = new(1f, 1f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(first);
        animation.Keyframes.Add(second);
        animation.Seek(0.25f, false);
        Assert.That(first.TryCaptureAuthoredState(out var before), Is.True);
        float linear = animation.GetValue(0.5f);
        var step = before with { InterpolationOut = EVectorInterpType.Step };

        Assert.That(first.TryApplyAuthoredState(before, step,
            animation.Keyframes), Is.True);
        Assert.That(first.TryCaptureAuthoredState(out var committed), Is.True);
        Assert.That(committed, Is.EqualTo(step));
        Assert.That(animation.GetValue(0.5f), Is.Not.EqualTo(linear));
        Assert.That(animation.CurrentTime, Is.EqualTo(0.25f));

        Assert.That(first.TryApplyAuthoredState(step, before,
            animation.Keyframes), Is.True);
        Assert.That(first.TryCaptureAuthoredState(out var restored), Is.True);
        Assert.That(restored, Is.EqualTo(before));
        Assert.That(animation.GetValue(0.5f), Is.EqualTo(linear).Within(0.0001f));
        Assert.That(animation.CurrentTime, Is.EqualTo(0.25f));
    }

    [Test]
    public void ConditionalCommitOwnsCompleteStateEvenWhenTrackObserverThrows()
    {
        PropAnimFloat animation = new(2, false, true);
        FloatKeyframe key = new(0.25f, 1, 0, EVectorInterpType.Linear);
        animation.Keyframes.Add(key);
        Assert.That(key.TryCaptureAuthoredState(out var before), Is.True);
        var after = before with { OutTangent = 2f };
        animation.Keyframes.Changed += Throw;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                key.TryApplyAuthoredState(before, after, animation.Keyframes));
            Assert.That(key.TryCaptureAuthoredState(out var committed), Is.True);
            Assert.That(committed, Is.EqualTo(after));
        }
        finally { animation.Keyframes.Changed -= Throw; }
        Assert.That(key.TryApplyAuthoredState(after, before,
            animation.Keyframes), Is.True);
        Assert.That(key.TryCaptureAuthoredState(out var restored), Is.True);
        Assert.That(restored, Is.EqualTo(before));

        static void Throw(BaseKeyframeTrack _) =>
            throw new InvalidOperationException("observer failed");
    }

    [Test]
    public void UndefinedInterpolationCannotCommitOrBeCaptured()
    {
        FloatKeyframe key = new(0.25f, 1, 0, EVectorInterpType.Linear);
        Assert.That(key.TryCaptureAuthoredState(out var before), Is.True);
        var invalid = before with { InterpolationOut = (EVectorInterpType)999 };
        Assert.That(key.TryApplyAuthoredState(before, invalid, null), Is.False);
        Assert.That(key.TryCaptureAuthoredState(out var unchanged), Is.True);
        Assert.That(unchanged, Is.EqualTo(before));
        key.InterpolationTypeOut = (EVectorInterpType)999;
        Assert.That(key.TryCaptureAuthoredState(out _), Is.False);
    }

    [Test]
    public void PublicSynchronizedSetterRecoversLatchAfterNestedObserverThrows()
    {
        PropAnimFloat animation = new(2, false, true);
        FloatKeyframe key = new(0.25f, 1, 0, EVectorInterpType.Linear);
        animation.Keyframes.Add(key);
        animation.Keyframes.Changed += Throw;
        try
        {
            Assert.Throws<InvalidOperationException>(() => key.OutTangent = 2);
        }
        finally { animation.Keyframes.Changed -= Throw; }
        Assert.That(key.TryCaptureAuthoredState(out _), Is.True);
        key.OutTangent = 3;
        Assert.That(key.InTangent, Is.EqualTo(-3));

        static void Throw(BaseKeyframeTrack _) =>
            throw new InvalidOperationException("nested observer failed");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExactRemovalInvalidatesWarmedPlaybackEvenWhenNotificationThrows(bool throwObserver)
    {
        PropAnimFloat animation = new(2, false, true);
        FloatKeyframe first = new(0f, 0f, 0f, EVectorInterpType.Linear);
        FloatKeyframe middle = new(1f, 100f, 0f, EVectorInterpType.Linear);
        FloatKeyframe last = new(2f, 0f, 0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(first);
        animation.Keyframes.Add(middle);
        animation.Keyframes.Add(last);
        animation.Seek(1f, wrapLooped: false);
        Assert.That(animation.CurrentPosition, Is.EqualTo(100f));
        if (throwObserver)
            animation.Keyframes.Changed += Throw;
        try
        {
            if (throwObserver)
                Assert.Throws<InvalidOperationException>(() => Edit([first, middle, last], [first, last]));
            else
                Assert.That(Edit([first, middle, last], [first, last]), Is.True);
            animation.Seek(1f, wrapLooped: false);
            Assert.That(animation.CurrentPosition, Is.Zero);
        }
        finally
        {
            if (throwObserver)
                animation.Keyframes.Changed -= Throw;
        }
        Assert.That(Edit([first, last], [first, middle, last]), Is.True);
        animation.Seek(1f, wrapLooped: false);
        Assert.That(animation.CurrentPosition, Is.EqualTo(100f));

        bool Edit(Keyframe[] expected, Keyframe[] desired)
            => animation.Keyframes.TryApplyExactKeyEdit(expected, desired, middle,
                middle.Second, middle.AuthoredFrameIndex, middle.Second, middle.AuthoredFrameIndex);
        static void Throw(BaseKeyframeTrack _) => throw new InvalidOperationException("Observer failed");
    }

    [Test]
    public void ExactTrackEditRejectsForeignKeyAndPublishesCompleteRemovalBeforeCallback()
    {
        KeyframeTrack<FloatKeyframe> track = new()
        {
            LengthInSeconds = 2f
        };
        FloatKeyframe first = new(0.25f, 1f, 0f,
            EVectorInterpType.Linear);
        FloatKeyframe second = new(0.75f, 2f, 0f,
            EVectorInterpType.Linear);
        track.Add(first);
        track.Add(second);
        KeyframeTrack<FloatKeyframe> foreign = new()
        {
            LengthInSeconds = 2f
        };
        FloatKeyframe foreignKey = new(0.5f, 3f, 0f,
            EVectorInterpType.Linear);
        foreign.Add(foreignKey);
        Assert.That(track.HasExactOrder([first, second]), Is.True,
            "The fixture must keep its authored order before rejection.");
        ulong generation = track.ExactEditGeneration;
        Assert.That(track.TryApplyExactKeyEdit([first, second],
            [first, foreignKey], second, second.Second,
            second.AuthoredFrameIndex, second.Second,
            second.AuthoredFrameIndex), Is.False);
        Assert.That(track.ExactEditGeneration, Is.EqualTo(generation));
        Assert.That(track.HasExactOrder([first, second]), Is.True);
        track.Changed += Changed;
        Assert.Throws<InvalidOperationException>(() =>
            track.TryApplyExactKeyEdit([first, second], [first], second,
                second.Second, second.AuthoredFrameIndex,
                second.Second, second.AuthoredFrameIndex));
        Assert.That(track.ExactEditGeneration, Is.GreaterThan(generation));
        Assert.That(track.HasExactOrder([first]), Is.True);
        Assert.That(second.OwningTrack, Is.Null);

        void Changed(BaseKeyframeTrack _) =>
            throw new InvalidOperationException("observer failed");
    }
}
