using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class InspectorFloatCurveTimeTests
{
    [SetUp] public void SetUp() => Undo.ClearHistory();
    [TearDown] public void TearDown() => Undo.ClearHistory();

    private static InspectorFloatCurveLengthSelection Capture(PropAnimFloat first, PropAnimFloat second)
    {
        var property = typeof(InspectorFloatCurveTestOwner).GetProperty(nameof(InspectorFloatCurveTestOwner.Curve))!;
        Assert.That(InspectorFloatCurveLengthSelection.TryCapture(property,
            [new InspectorFloatCurveTestOwner { Curve = first }, new InspectorFloatCurveTestOwner { Curve = second }],
            out var selection, out string reason), Is.True, reason);
        return selection!;
    }

    private static FloatKeyframe Add(PropAnimFloat curve, float second)
    {
        FloatKeyframe key = new(second, 2f, 0f, EVectorInterpType.Hermite);
        key.WeightedMode = EKeyframeWeightedMode.Both;
        key.InWeight = 0.2f;
        key.OutWeight = 0.8f;
        curve.Keyframes.Add(key);
        return key;
    }

    [TestCase(0.25f)]
    [TestCase(2f)]
    [TestCase(2.5f)]
    public void MovePreservesPayloadAndCanonicalOrderAcrossHistory(float second)
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 1), peer = Add(first, 2), b = Add(other, 1.5f);
        a.AuthoredFrameIndex = 30;
        b.AuthoredFrameIndex = 45;
        first.Seek(0.25f, false);
        other.Seek(0.75f, false);
        Keyframe[] before = AnimationClipAuthoringActions.CaptureOrder(first.Keyframes)!;
        Keyframe[] expected = AnimationClipAuthoringActions.OrderWithMovedKey(before, a, second);
        Assert.That(a.TryCaptureAuthoredState(out var payload), Is.True);
        var group = Capture(first, other);
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], second), Is.True);
        Assert.That(AnimationClipAuthoringActions.MatchesOrder(first.Keyframes, expected), Is.True);
        Assert.That((a.Second, b.Second, a.AuthoredFrameIndex, b.AuthoredFrameIndex), Is.EqualTo((second, second, -1, -1)));
        Assert.That(a.TryCaptureAuthoredState(out var after), Is.True);
        Assert.That(after, Is.EqualTo(payload));
        Assert.That(peer.Second, Is.EqualTo(2));
        Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(AnimationClipAuthoringActions.MatchesOrder(first.Keyframes, before), Is.True);
        Assert.That((a.Second, b.Second, a.AuthoredFrameIndex, b.AuthoredFrameIndex), Is.EqualTo((1f, 1.5f, 30, 45)));
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(AnimationClipAuthoringActions.MatchesOrder(first.Keyframes, expected), Is.True);
        Assert.That((first.CurrentTime, other.CurrentTime), Is.EqualTo((0.25f, 0.75f)));
    }

    [Test]
    public void MixedNoopRetainsFrameAndDoesNotPublishThenAllNoopAddsNoHistory()
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 2), b = Add(other, 1);
        a.AuthoredFrameIndex = 60;
        ulong generation = first.Keyframes.ExactEditGeneration;
        ulong revision = first.Keyframes.ChangedNotificationRevision;
        var group = Capture(first, other);
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2), Is.True);
        Assert.That((a.AuthoredFrameIndex, first.Keyframes.ExactEditGeneration, first.Keyframes.ChangedNotificationRevision),
            Is.EqualTo((60, generation, revision)));
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2), Is.False);
        Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That((a.Second, b.Second), Is.EqualTo((2f, 1f)));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(-1f)]
    [TestCase(2.5f)]
    public void InvalidDestinationRejectsBeforeAnyPeerWrite(float second)
    {
        PropAnimFloat first = new(3, false, true), other = new(2, false, true);
        FloatKeyframe a = Add(first, 1), b = Add(other, 1);
        ulong generation = first.Keyframes.ExactEditGeneration;
        Assert.That(InspectorFloatCurveActions.SetTime(Capture(first, other), [a, b], second), Is.False);
        Assert.That(a.Second, Is.EqualTo(1));
        Assert.That(first.Keyframes.ExactEditGeneration, Is.EqualTo(generation));
        Assert.That(Undo.PendingUndo, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LaterPeerBudgetRejectsBeforeEarlierWrite(bool structural)
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 1), b = Add(other, 1);
        SetCounter(other.Keyframes, structural, ulong.MaxValue - 1);
        Assert.That(InspectorFloatCurveActions.SetTime(Capture(first, other), [a, b], 2), Is.False);
        Assert.That((a.Second, b.Second), Is.EqualTo((1f, 1f)));
        Assert.That(Undo.PendingUndo, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ObserverFailureCompensatesWithReservedRevisionBudget(bool nearLimit)
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 1), b = Add(other, 1);
        a.AuthoredFrameIndex = 30;
        if (nearLimit)
        {
            SetCounter(first.Keyframes, true, ulong.MaxValue - 2);
            SetCounter(first.Keyframes, false, ulong.MaxValue - 3);
        }
        var group = Capture(first, other);
        bool threw = false;
        first.Keyframes.Changed += _ =>
        {
            if (threw) return;
            threw = true;
            Assert.That((a.Second, a.AuthoredFrameIndex), Is.EqualTo((2f, -1)));
            throw new InvalidOperationException("After the exact move committed.");
        };
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2), Is.False);
        Assert.That((a.Second, b.Second, a.AuthoredFrameIndex), Is.EqualTo((1f, 1f, 30)));
        Assert.That(Undo.PendingUndo, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PeerCallbackAwayAndBackPreventsLaterMove(bool structural)
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 1), b = Add(other, 1);
        var group = Capture(first, other);
        bool changed = false;
        first.Keyframes.Changed += _ =>
        {
            if (changed) return;
            changed = true;
            if (structural)
            {
                Keyframe[] order = AnimationClipAuthoringActions.CaptureOrder(other.Keyframes)!;
                Assert.That(other.Keyframes.TryApplyExactKeyEdit(order, order, b, 1, -1, 1.5f, -1), Is.True);
                Assert.That(other.Keyframes.TryApplyExactKeyEdit(order, order, b, 1.5f, -1, 1, -1), Is.True);
            }
            else { b.OutValue = 7; b.OutValue = 2; }
        };
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2), Is.False);
        Assert.That(changed, Is.True);
        Assert.That(b.Second, Is.EqualTo(1));
        Assert.That(Undo.PendingUndo, Is.Empty);
    }

    [Test]
    public void ConsecutiveMovesRestoreExactFramesInHistoryOrder()
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 0.5f), b = Add(other, 1);
        a.AuthoredFrameIndex = 15;
        var group = Capture(first, other);
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2), Is.True);
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2.5f), Is.True);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That((a.Second, a.AuthoredFrameIndex, b.Second), Is.EqualTo((0.5f, 15, 1f)));
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That((a.Second, b.Second), Is.EqualTo((2.5f, 2.5f)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplayPreservesPeerCallbackEditAndRejectsLaterMove(bool duringRedo)
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 1), b = Add(other, 1);
        var group = Capture(first, other);
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 2), Is.True);
        if (duringRedo) Assert.That(Undo.TryUndo(), Is.True);
        bool changed = false;
        (duringRedo ? first : other).Keyframes.Changed += _ =>
        {
            if (changed) return;
            changed = true;
            (duringRedo ? b : a).OutValue = 77;
        };
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (duringRedo) Undo.TryRedo();
            else Undo.TryUndo();
        });
        Assert.That(changed, Is.True);
        Assert.That((duringRedo ? b : a).OutValue, Is.EqualTo(77));
        Assert.That((duringRedo ? b : a).Second, Is.EqualTo(duringRedo ? 1 : 2));
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (duringRedo) Undo.TryRedo();
            else Undo.TryUndo();
        });
    }

    [Test]
    public void ExistingBeyondEndPeerSurvivesMoveAndUndo()
    {
        PropAnimFloat first = new(3, false, true), other = new(3, false, true);
        FloatKeyframe a = Add(first, 1), peer = Add(first, 2.5f), b = Add(other, 1);
        first.SetLength(2, false);
        var group = Capture(first, other);
        Assert.That(InspectorFloatCurveActions.SetTime(group, [a, b], 1.5f), Is.True);
        Assert.That(peer.Second, Is.EqualTo(2.5f));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That((a.Second, peer.Second), Is.EqualTo((1f, 2.5f)));
    }

    private static void SetCounter(BaseKeyframeTrack track, bool structural, ulong value)
        => typeof(BaseKeyframeTrack).GetField(structural
            ? "<ExactEditGeneration>k__BackingField" : "_changedNotificationRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(track, value);
}
