using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class InspectorFloatCurveRemovalMarksTests
{
    private static PropAnimFloat Curve(out FloatKeyframe key)
    {
        PropAnimFloat curve = new(2, false, true);
        key = new(1, 2, 0, EVectorInterpType.Hermite);
        curve.Keyframes.Add(key);
        return curve;
    }

    [Test]
    public void EveryCurveNeedsAnExplicitExactTarget()
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        FloatKeyframe equalTime = new(1, 2, 0, EVectorInterpType.Hermite);
        a.Keyframes.Add(equalTime);
        var marks = new InspectorFloatCurveKeyMarks([a, b], () => true);
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.Mark(0, second), Is.False);
        Assert.That(marks.Mark(0, equalTime), Is.True);
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.Mark(1, second), Is.True);
        Assert.That(marks.TryCapture(out var targets, out _), Is.True);
        Assert.That(targets[0], Is.SameAs(equalTime));
        Assert.That(targets[0], Is.Not.SameAs(first));
        Assert.That(targets[1], Is.SameAs(second));
        marks.Unmark(0);
        Assert.That(marks.TryCapture(out _, out _), Is.False);
    }

    [Test]
    public void PayloadChangesInvalidateOnlyTheirMark()
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        var marks = new InspectorFloatCurveKeyMarks([a, b], () => true);
        marks.Mark(0, first);
        marks.Mark(1, second);
        first.OutValue = 9;
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.GetMarkedKey(0), Is.Null);
        Assert.That(marks.GetMarkedKey(1), Is.SameAs(second));
    }

    [Test]
    public void ValueWriteAwayAndBackInvalidatesTheOldMark()
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        var marks = new InspectorFloatCurveKeyMarks([a, b], () => true);
        marks.Mark(0, first);
        marks.Mark(1, second);
        float before = first.OutValue;
        first.OutValue = 9;
        first.OutValue = before;
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.GetMarkedKey(0), Is.Null);
        Assert.That(marks.GetMarkedKey(1), Is.SameAs(second));
    }

    [Test]
    public void ExactTrackEditAwayAndBackInvalidatesTheOldMark()
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        var marks = new InspectorFloatCurveKeyMarks([a, b], () => true);
        marks.Mark(0, first);
        marks.Mark(1, second);
        Keyframe[] order = [first];
        Assert.That(a.Keyframes.TryApplyExactKeyEdit(order, [], first, 1, -1, 1, -1), Is.True);
        Assert.That(a.Keyframes.TryApplyExactKeyEdit([], order, first, 1, -1, 1, -1), Is.True);
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.GetMarkedKey(0), Is.Null);
    }

    [Test]
    public void CompletionCannotClearANewerMarkingIntent()
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        var marks = new InspectorFloatCurveKeyMarks([a, b], () => true);
        marks.Mark(0, first);
        marks.Mark(1, second);
        Assert.That(marks.TryCapture(out _, out ulong intent), Is.True);
        marks.Unmark(0);
        marks.Mark(0, first);
        marks.ClearIfCurrent(intent);
        Assert.That(marks.TryCapture(out _, out ulong newer), Is.True);
        marks.ClearIfCurrent(newer);
        Assert.That(marks.MarkedCount, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedOwnerOrCurveLifetimeRejectsMarks(bool changeLifetime)
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        bool current = true;
        var marks = new InspectorFloatCurveKeyMarks([a, b], () => current);
        marks.Mark(0, first);
        marks.Mark(1, second);
        if (changeLifetime) a.Generate();
        else current = false;
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.MarkedCount, Is.Zero);
    }

    [Test]
    public void SourceAdmissionCannotOverwriteAReentrantUnmark()
    {
        var a = Curve(out var first);
        var b = Curve(out _);
        InspectorFloatCurveKeyMarks? marks = null;
        bool change = false;
        marks = new([a, b], () =>
        {
            if (change) { change = false; marks!.Unmark(0); }
            return true;
        });
        marks.Mark(0, first);
        change = true;
        Assert.That(marks.Mark(0, first), Is.False);
        Assert.That(marks.GetMarkedKey(0), Is.Null);
    }

    [Test]
    public void CaptureCannotAdoptNewerCompleteMarksFromSourceAdmission()
    {
        var a = Curve(out var first);
        var b = Curve(out var second);
        InspectorFloatCurveKeyMarks? marks = null;
        bool change = false;
        marks = new([a, b], () =>
        {
            if (change)
            {
                change = false;
                marks!.Unmark(0);
                marks.Mark(0, first);
            }
            return true;
        });
        marks.Mark(0, first);
        marks.Mark(1, second);
        change = true;
        Assert.That(marks.TryCapture(out _, out _), Is.False);
        Assert.That(marks.TryCapture(out var targets, out _), Is.True);
        Assert.That(targets[0], Is.SameAs(first));
    }

    [Test]
    public void UnsupportedCurveCountsAndDuplicateCurvesAreRejected()
    {
        var a = Curve(out _);
        Assert.Throws<ArgumentException>(() => new InspectorFloatCurveKeyMarks([a], () => true));
        Assert.Throws<ArgumentException>(() => new InspectorFloatCurveKeyMarks([a, a], () => true));
        var curves = Enumerable.Range(0, 65).Select(index => Curve(out _)).ToArray();
        Assert.Throws<ArgumentException>(() => new InspectorFloatCurveKeyMarks(curves, () => true));
    }
}
