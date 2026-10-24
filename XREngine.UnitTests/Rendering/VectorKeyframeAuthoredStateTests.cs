using NUnit.Framework;
using XREngine.Animation;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VectorKeyframeAuthoredStateTests
{
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
}
