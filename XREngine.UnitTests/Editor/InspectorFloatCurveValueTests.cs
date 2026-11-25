using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class InspectorFloatCurveValueTests
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
    public void MixedNoopAndSynchronizedValuePreserveFullPayloadAcrossHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(3, false, true), second = new(3, false, true);
            FloatKeyframe a = new(1f, 7f, 7f, 2f, 3f, EVectorInterpType.Hermite);
            FloatKeyframe b = new(1f, 2f, 4f, 5f, 6f, EVectorInterpType.Hermite);
            b.SyncInOutValues = true;
            b.WeightedMode = EKeyframeWeightedMode.Both;
            b.InWeight = 0.2f;
            b.OutWeight = 0.8f;
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            first.Seek(0.25f, false);
            second.Seek(0.75f, false);
            Assert.That(a.TryCaptureAuthoredState(out var aBefore), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var bBefore), Is.True);
            InspectorFloatCurveTestOwner firstOwner = new() { Curve = first };
            InspectorFloatCurveTestOwner sharedOwner = new() { Curve = first };
            InspectorFloatCurveTestOwner secondOwner = new() { Curve = second };
            var selection = Capture(firstOwner, sharedOwner, secondOwner, firstOwner);
            Assert.That(selection.Curves, Has.Length.EqualTo(2));
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.True);
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That(a.TryCaptureAuthoredState(out var aAfter), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var bAfter), Is.True);
            Assert.That(aAfter, Is.EqualTo(aBefore));
            Assert.That((bAfter.InValue, bAfter.OutValue), Is.EqualTo((7f, 7f)));
            Assert.That((bAfter.InTangent, bAfter.OutTangent, bAfter.WeightedMode,
                bAfter.InWeight, bAfter.OutWeight),
                Is.EqualTo((bBefore.InTangent, bBefore.OutTangent, bBefore.WeightedMode,
                    bBefore.InWeight, bBefore.OutWeight)));
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var bRestored), Is.True);
            Assert.That(bRestored, Is.EqualTo(bBefore));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var bRedone), Is.True);
            Assert.That(bRedone, Is.EqualTo(bAfter));
            Assert.That((first.CurrentTime, second.CurrentTime), Is.EqualTo((0.25f, 0.75f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ConsecutiveAcceptedValueEditsUndoAndRedoInOrder()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.True);
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 8f), Is.True);
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(2));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((7f, 7f)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 2f)));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((8f, 8f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void WrongTargetsStaleOwnerAndInvalidValueDoNotStartHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            InspectorFloatCurveTestOwner one = new() { Curve = first };
            InspectorFloatCurveTestOwner two = new() { Curve = second };
            var selection = Capture(one, two);
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [b, a], 7f), Is.False);
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a], 7f), Is.False);
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, a], 7f), Is.False);
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], float.NaN), Is.False);
            two.Curve = new PropAnimFloat(2, false, true);
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ValueCountLimitsRejectOversizedGroups()
    {
        Assert.That(InspectorFloatCurveValueEdit.FitsCounts(2, 2), Is.True);
        Assert.That(InspectorFloatCurveValueEdit.FitsCounts(64, 100_000), Is.True);
        Assert.That(InspectorFloatCurveValueEdit.FitsCounts(64, 100_001), Is.False);
        Assert.That(InspectorFloatCurveValueEdit.FitsCounts(65, 65), Is.False);
    }

    [Test]
    public void SixtyFiveDistinctCurvesRejectBeforeAnyValueOrHistoryChange()
    {
        Undo.ClearHistory();
        try
        {
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
            Assert.That(InspectorFloatCurveActions.SetValue(selection, targets, 7f), Is.False);
            Assert.That(owners.All(owner => owner.Curve!.Keyframes.First!.OutValue == 1f),
                Is.True);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LaterMemberNearRevisionLimitRejectsBeforeEarlierValueWrite()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            SetNotificationRevision(second.Keyframes, ulong.MaxValue - 2);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void BoundaryObserverThrowLeavesRevisionForOwnedCompensation()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            SetNotificationRevision(first.Keyframes, ulong.MaxValue - 3);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool thrown = false;
            first.Keyframes.Changed += _ =>
            {
                if (thrown) return;
                thrown = true;
                throw new InvalidOperationException("observer failure");
            };
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That(thrown, Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 2f)));
            Assert.That(first.Keyframes.ChangedNotificationRevision,
                Is.EqualTo(ulong.MaxValue - 1));
            Assert.That(first.Keyframes.CanAdvanceChangedNotificationRevision, Is.True);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void PeerAwayAndBackNotificationRejectsLaterWriteWithoutHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool changed = false;
            first.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                b.OutValue = 99f;
                b.OutValue = 2f;
            };
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That(changed, Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void PeerCallbackValueWinsAndLaterGroupedWriteNeverStarts()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
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
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That(changed, Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 99f)));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(b.OutValue, Is.EqualTo(99f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ObserverThrowAfterCompleteCommitCompensatesTheOwnedValue()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool thrown = false;
            first.Keyframes.Changed += _ =>
            {
                if (thrown) return;
                thrown = true;
                throw new InvalidOperationException("observer failure");
            };
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That(thrown, Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((1f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void LastObserverAwayAndBackCannotRearmConflictedBatch()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool changed = false;
            second.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                a.OutValue = 8f;
                a.OutValue = 7f;
                b.OutValue = 8f;
                b.OutValue = 7f;
            };
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.False);
            Assert.That(changed, Is.True);
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((7f, 7f)));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That((a.OutValue, b.OutValue), Is.EqualTo((7f, 7f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplayPeerChangeRejectsLaterWrite(bool duringRedo)
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(2, false, true), second = new(2, false, true);
            FloatKeyframe a = new(1f, 1f, 0, EVectorInterpType.Linear);
            FloatKeyframe b = new(1f, 2f, 0, EVectorInterpType.Linear);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.SetValue(selection, [a, b], 7f), Is.True);
            if (duringRedo) Assert.That(Undo.TryUndo(), Is.True);
            bool changed = false;
            (duringRedo ? first : second).Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                (duringRedo ? b : a).OutValue = 77f;
            };
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (duringRedo) Undo.TryRedo();
                else Undo.TryUndo();
            });
            Assert.That(changed, Is.True);
            Assert.That((duringRedo ? b : a).OutValue, Is.EqualTo(77f));
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (duringRedo) Undo.TryRedo();
                else Undo.TryUndo();
            });
        }
        finally { Undo.ClearHistory(); }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(14)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    public void GroupedHandleEditsMatchPublicSettersAndRestoreCompleteHistory(int operation)
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(3, false, true), second = new(3, false, true);
            FloatKeyframe a = new(1f, 2f, 4f, 5f, 6f, EVectorInterpType.Hermite);
            FloatKeyframe b = new(2f, 3f, 7f, 2f, 9f, EVectorInterpType.Hermite);
            a.RestorePublishedHandles(2, 4, 5, 6, EVectorInterpType.Hermite,
                EVectorInterpType.Hermite, false, false, false);
            b.SyncInOutValues = true;
            b.SyncInOutTangentDirections = true;
            b.SyncInOutTangentMagnitudes = true;
            a.WeightedMode = b.WeightedMode = operation >= 16
                ? EKeyframeWeightedMode.None : EKeyframeWeightedMode.Both;
            a.InWeight = b.InWeight = 0.2f;
            a.OutWeight = b.OutWeight = 0.8f;
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            a.AuthoredFrameIndex = 30;
            b.AuthoredFrameIndex = 60;
            Assert.That(a.TryCaptureAuthoredState(out var beforeA), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var beforeB), Is.True);
            var expectedA = ExpectedHandleState(beforeA, operation);
            var expectedB = ExpectedHandleState(beforeB, operation);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            bool changed = operation switch
            {
                0 => InspectorFloatCurveActions.SetValue(selection, [a, b], 11),
                1 => InspectorFloatCurveActions.SetIncomingValue(selection, [a, b], 11),
                2 or 3 => InspectorFloatCurveActions.SetTangent(selection, [a, b], operation == 3, 11),
                4 or 5 => InspectorFloatCurveActions.SetInterpolation(selection, [a, b], operation == 5, EVectorInterpType.Linear),
                6 => InspectorFloatCurveActions.SetSyncValues(selection, [a, b], true),
                7 => InspectorFloatCurveActions.SetSyncDirections(selection, [a, b], true),
                8 => InspectorFloatCurveActions.SetSyncMagnitudes(selection, [a, b], true),
                9 or 10 => InspectorFloatCurveActions.SetWeightedSide(selection, [a, b], operation == 10, false),
                11 or 12 => InspectorFloatCurveActions.SetWeight(selection, [a, b], operation == 12, 0.6f),
                13 => InspectorFloatCurveActions.SetSyncValues(selection, [a, b], false),
                14 => InspectorFloatCurveActions.SetSyncDirections(selection, [a, b], false),
                15 => InspectorFloatCurveActions.SetSyncMagnitudes(selection, [a, b], false),
                _ => InspectorFloatCurveActions.SetWeightedSide(selection, [a, b], operation == 17, true),
            };
            Assert.That(changed, Is.True);
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That(a.TryCaptureAuthoredState(out var afterA), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var afterB), Is.True);
            Assert.That(afterA, Is.EqualTo(expectedA));
            Assert.That(afterB, Is.EqualTo(expectedB));
            Assert.That((a.Second, a.AuthoredFrameIndex, b.Second, b.AuthoredFrameIndex),
                Is.EqualTo((1f, 30, 2f, 60)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(a.TryCaptureAuthoredState(out var restoredA), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var restoredB), Is.True);
            Assert.That(restoredA, Is.EqualTo(beforeA));
            Assert.That(restoredB, Is.EqualTo(beforeB));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(a.TryCaptureAuthoredState(out var redoneA), Is.True);
            Assert.That(b.TryCaptureAuthoredState(out var redoneB), Is.True);
            Assert.That(redoneA, Is.EqualTo(expectedA));
            Assert.That(redoneB, Is.EqualTo(expectedB));
        }
        finally { Undo.ClearHistory(); }
    }

    private static VectorKeyframeAuthoredState<float> ExpectedHandleState(
        VectorKeyframeAuthoredState<float> before, int operation)
    {
        FloatKeyframe key = new();
        key.RestorePublishedHandles(before.InValue, before.OutValue, before.InTangent,
            before.OutTangent, before.InterpolationIn, before.InterpolationOut,
            before.SyncValues, before.SyncDirections, before.SyncMagnitudes);
        Assert.That(key.TryRestoreAuthoredWeights(before.WeightedMode, before.InWeight, before.OutWeight), Is.True);
        switch (operation)
        {
            case 0: key.OutValue = 11; break;
            case 1: key.InValue = 11; break;
            case 2: key.InTangent = 11; break;
            case 3: key.OutTangent = 11; break;
            case 4: key.InterpolationTypeIn = EVectorInterpType.Linear; break;
            case 5: key.InterpolationTypeOut = EVectorInterpType.Linear; break;
            case 6: key.SyncInOutValues = true; break;
            case 7: key.SyncInOutTangentDirections = true; break;
            case 8: key.SyncInOutTangentMagnitudes = true; break;
            case 9: key.WeightedMode &= ~EKeyframeWeightedMode.In; break;
            case 10: key.WeightedMode &= ~EKeyframeWeightedMode.Out; break;
            case 11: key.InWeight = 0.6f; break;
            case 12: key.OutWeight = 0.6f; break;
            case 13: key.SyncInOutValues = false; break;
            case 14: key.SyncInOutTangentDirections = false; break;
            case 15: key.SyncInOutTangentMagnitudes = false; break;
            case 16: key.WeightedMode |= EKeyframeWeightedMode.In; break;
            case 17: key.WeightedMode |= EKeyframeWeightedMode.Out; break;
        }
        Assert.That(key.TryCaptureAuthoredState(out var expected), Is.True);
        return expected;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void IneligibleWeightedPeerRejectsBeforeAnyCommit(bool setWeight)
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(3, false, true), second = new(3, false, true);
            FloatKeyframe a = new(1f, 2f, 0, EVectorInterpType.Hermite);
            FloatKeyframe b = new(1f, 3f, 0, EVectorInterpType.Linear);
            a.WeightedMode = b.WeightedMode = EKeyframeWeightedMode.Both;
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            Assert.That(a.TryCaptureAuthoredState(out var before), Is.True);
            ulong revision = first.Keyframes.ChangedNotificationRevision;
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(setWeight
                ? InspectorFloatCurveActions.SetWeight(selection, [a, b], false, 0.6f)
                : InspectorFloatCurveActions.SetWeightedSide(selection, [a, b], false, false), Is.False);
            Assert.That(a.TryCaptureAuthoredState(out var after), Is.True);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(first.Keyframes.ChangedNotificationRevision, Is.EqualTo(revision));
            Assert.That(Undo.PendingUndo, Is.Empty);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void SynchronizationOverflowRejectsBeforeEarlierPeerCommit()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat first = new(3, false, true), second = new(3, false, true);
            FloatKeyframe a = new(1f, 2f, 4f, 0, 0, EVectorInterpType.Hermite);
            FloatKeyframe b = new(1f, float.MaxValue, float.MaxValue, 0, 0, EVectorInterpType.Hermite);
            first.Keyframes.Add(a);
            second.Keyframes.Add(b);
            Assert.That(a.TryCaptureAuthoredState(out var before), Is.True);
            var selection = Capture(new() { Curve = first }, new() { Curve = second });
            Assert.That(InspectorFloatCurveActions.SetSyncValues(selection, [a, b], true), Is.False);
            Assert.That(a.TryCaptureAuthoredState(out var after), Is.True);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(Undo.PendingUndo, Is.Empty);
        }
        finally { Undo.ClearHistory(); }
    }

    private static void SetNotificationRevision(BaseKeyframeTrack track, ulong revision)
        => typeof(BaseKeyframeTrack).GetField("_changedNotificationRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(track, revision);
}
