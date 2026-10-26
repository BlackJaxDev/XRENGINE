using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class InspectorFloatCurveRemoveTests
{
    private static InspectorFloatCurveLengthSelection Capture(params InspectorFloatCurveTestOwner[] owners)
    {
        var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        Assert.That(InspectorFloatCurveLengthSelection.TryCapture(property,
            [.. owners], out var selection, out string reason), Is.True, reason);
        return selection!;
    }

    [Test]
    public void RemovesExactSameTimeKeysOnceAndRestoresPayloadAcrossConsecutiveHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(3, false, true), second = new(3, false, true);
            FloatKeyframe a = new(1f, 2f, 3f, EVectorInterpType.Hermite);
            FloatKeyframe b = new(1f, 4f, 5f, EVectorInterpType.Linear);
            FloatKeyframe c = new(1f, 6f, 7f, EVectorInterpType.Hermite);
            FloatKeyframe d = new(1f, 8f, 9f, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            first.Keyframes.Add(b);
            second.Keyframes.Add(c);
            second.Keyframes.Add(d);
            Keyframe[] firstOrder = AnimationClipAuthoringActions.CaptureOrder(first.Keyframes)!;
            Keyframe[] secondOrder = AnimationClipAuthoringActions.CaptureOrder(second.Keyframes)!;
            Assert.That(first.Keyframes.TryApplyExactKeyEdit(firstOrder, firstOrder,
                a, 1f, -1, 1f, 12), Is.True);
            Assert.That(second.Keyframes.TryApplyExactKeyEdit(secondOrder, secondOrder,
                d, 1f, -1, 1f, 24), Is.True);
            first.Seek(0.25f, false);
            second.Seek(0.75f, false);
            Assert.That(a.TryCaptureAuthoredState(out var aState), Is.True);
            Assert.That(d.TryCaptureAuthoredState(out var dState), Is.True);
            InspectorFloatCurveTestOwner firstOwner = new() { Curve = first };
            InspectorFloatCurveTestOwner sharedOwner = new() { Curve = first };
            InspectorFloatCurveTestOwner secondOwner = new() { Curve = second };
            var selection = Capture(firstOwner, sharedOwner, secondOwner, firstOwner);
            Assert.That(selection.Curves, Has.Length.EqualTo(2));

            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, d]), Is.True);
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((1, 1)));
            Assert.That(first.Keyframes.First, Is.SameAs(b));
            Assert.That(second.Keyframes.First, Is.SameAs(c));
            Assert.That(a.OwningTrack, Is.Null);
            Assert.That(d.OwningTrack, Is.Null);
            Assert.That((a.AuthoredFrameIndex, d.AuthoredFrameIndex), Is.EqualTo((12, 24)));
            Assert.That((first.CurrentTime, second.CurrentTime), Is.EqualTo((0.25f, 0.75f)));
            Assert.That(InspectorFloatCurveActions.Remove(selection, [b, c]), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((2, 2)));
            Assert.That(a.TryCaptureAuthoredState(out var aRestored), Is.True);
            Assert.That(d.TryCaptureAuthoredState(out var dRestored), Is.True);
            Assert.That((aRestored, dRestored), Is.EqualTo((aState, dState)));
            Assert.That((a.AuthoredFrameIndex, d.AuthoredFrameIndex), Is.EqualTo((12, 24)));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((0, 0)));
            Assert.That((first.CurrentTime, second.CurrentTime), Is.EqualTo((0.25f, 0.75f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void WrongTargetOrderOrReplacedOwnerCannotStartHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            InspectorFloatCurveTestOwner one = new() { Curve = first };
            InspectorFloatCurveTestOwner two = new() { Curve = second };
            var selection = Capture(one, two);
            Assert.That(InspectorFloatCurveActions.Remove(selection, [b, a]), Is.False);
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a]), Is.False);
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, a]), Is.False);
            two.Curve = new PropAnimFloat(2, false, true);
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, b]), Is.False);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((1, 1)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void RemovalRestoresKeysBeyondLengthsAfterNonstretchShrink()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1.5f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1.75f, 3f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            first.SetLength(0.5f, false);
            second.SetLength(0.75f, false);
            Assert.That((a.Second, b.Second), Is.EqualTo((1.5f, 1.75f)));
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, b]), Is.True);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((0, 0)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.Keyframes.First, Is.SameAs(a));
            Assert.That(second.Keyframes.First, Is.SameAs(b));
            Assert.That((a.Second, b.Second), Is.EqualTo((1.5f, 1.75f)));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((first.LengthInSeconds, second.LengthInSeconds),
                Is.EqualTo((0.5f, 0.75f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void UnsortedCapturedOrderRejectsBeforeAnyRemovalOrHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(0.5f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            FloatKeyframe c = new(1f, 4f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            first.Keyframes.Add(b);
            second.Keyframes.Add(c);
            // Keep exact list links, but make the second time precede the first.
            b.SetExactEditState(first.Keyframes, a, null, 0.25f, -1);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, c]), Is.False);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((2, 1)));
            Assert.That(first.Keyframes.First, Is.SameAs(a));
            Assert.That(first.Keyframes.Last, Is.SameAs(b));
            Assert.That(second.Keyframes.First, Is.SameAs(c));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void RemoveLimitsRejectOversizedGroupsBeforeHistory()
    {
        Undo.ClearHistory();
        try
        {
            Assert.That(InspectorFloatCurveRemoveEdit.FitsCounts(2, 2), Is.True);
            Assert.That(InspectorFloatCurveRemoveEdit.FitsCounts(64, 100_000), Is.True);
            Assert.That(InspectorFloatCurveRemoveEdit.FitsCounts(64, 100_001), Is.False);
            Assert.That(InspectorFloatCurveRemoveEdit.FitsCounts(65, 65), Is.False);
            InspectorFloatCurveTestOwner[] owners = Enumerable.Range(0, 65)
                .Select(_ =>
                {
                    PropAnimFloat curve = new(2, false, true);
                    curve.Keyframes.Add(new FloatKeyframe(1f, 1f, 0,
                        EVectorInterpType.Linear));
                    return new InspectorFloatCurveTestOwner { Curve = curve };
                }).ToArray();
            var selection = Capture(owners);
            FloatKeyframe[] targets = owners.Select(owner => owner.Curve!.Keyframes.First!)
                .ToArray();
            Assert.That(InspectorFloatCurveActions.Remove(selection, targets), Is.False);
            Assert.That(owners.All(owner => owner.Curve!.Keyframes.Count == 1), Is.True);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void PeerValueCallbackStopsLaterRemovalAndKeepsItsNewerValue()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool changed = false;
            first.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                b.OutValue = 99f;
            };
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, b]), Is.False);
            Assert.That(changed, Is.True);
            Assert.That(b.OutValue, Is.EqualTo(99f));
            Assert.That(second.Keyframes.First, Is.SameAs(b));
            Assert.That(first.Keyframes.First, Is.SameAs(a));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(b.OutValue, Is.EqualTo(99f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ChangedCallbackThrowAfterCommitRestoresOnlyTheOwnedRemoval()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool thrown = false;
            first.Keyframes.Changed += _ =>
            {
                if (thrown) return;
                thrown = true;
                throw new InvalidOperationException("Changed callback failed.");
            };
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, b]), Is.False);
            Assert.That(thrown, Is.True);
            Assert.That(first.Keyframes.First, Is.SameAs(a));
            Assert.That(second.Keyframes.First, Is.SameAs(b));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void UndoCallbackChangingDetachedPeerRejectsLaterWrite()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, b]), Is.True);
            bool changed = false;
            second.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                a.OutValue = 88f;
            };
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(changed, Is.True);
            Assert.That(a.OutValue, Is.EqualTo(88f));
            Assert.That(first.Keyframes.Count, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(a.OutValue, Is.EqualTo(88f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LastRemovalCallbackCannotRearmConflictedBatchFromMatchingValues()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 2f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool changed = false;
            second.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                FloatKeyframe x = new(0.5f, 4f, 0, EVectorInterpType.Linear);
                FloatKeyframe y = new(0.5f, 5f, 0, EVectorInterpType.Linear);
                Assert.That(first.Keyframes.TryApplyExactKeyEdit([], [x], x,
                    0.5f, -1, 0.5f, -1), Is.True);
                Assert.That(first.Keyframes.TryApplyExactKeyEdit([x], [], x,
                    0.5f, -1, 0.5f, -1), Is.True);
                Assert.That(second.Keyframes.TryApplyExactKeyEdit([], [y], y,
                    0.5f, -1, 0.5f, -1), Is.True);
                Assert.That(second.Keyframes.TryApplyExactKeyEdit([y], [], y,
                    0.5f, -1, 0.5f, -1), Is.True);
            };
            Assert.That(InspectorFloatCurveActions.Remove(selection, [a, b]), Is.False);
            Assert.That(changed, Is.True);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((0, 0)));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((0, 0)));
        }
        finally { Undo.ClearHistory(); }
    }
}
