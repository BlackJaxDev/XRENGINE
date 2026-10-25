using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class InspectorFloatCurveActionsTests
{
    [Test]
    public void WeightedHermiteEditsAndOrdinaryValueEditPreserveCompleteHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe first = new(0f, 0f, 5f, EVectorInterpType.Hermite);
            FloatKeyframe second = new(1f, 1f, -1f, EVectorInterpType.Hermite);
            curve.Keyframes.Add(first);
            curve.Keyframes.Add(second);
            curve.Seek(0.25f, false);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out string reason), Is.True, reason);
            Assert.That(first.TryCaptureAuthoredState(out var before), Is.True);
            float unweightedSample = curve.GetValue(0.4f);

            Assert.That(InspectorFloatCurveActions.SetWeightedSide(source!, first,
                true, true), Is.True);
            Assert.That(InspectorFloatCurveActions.SetWeight(source!, first,
                true, 0.8f), Is.True);
            Assert.That(curve.GetValue(0.4f), Is.Not.EqualTo(unweightedSample));
            Assert.That(InspectorFloatCurveActions.SetValue(source!, first, 2f), Is.True);
            Assert.That(first.TryCaptureAuthoredState(out var after), Is.True);
            Assert.That((after.WeightedMode, after.OutWeight, curve.CurrentTime),
                Is.EqualTo((EKeyframeWeightedMode.Out, 0.8f, 0.25f)));

            for (int index = 0; index < 3; index++)
                Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.TryCaptureAuthoredState(out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(before));
            for (int index = 0; index < 3; index++)
                Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(first.TryCaptureAuthoredState(out var redone), Is.True);
            Assert.That(redone, Is.EqualTo(after));
            Assert.That(curve.CurrentTime, Is.EqualTo(0.25f));
            Assert.That(InspectorFloatCurveActions.SetWeight(source!, first,
                true, 1.1f), Is.False);
            Assert.That(InspectorFloatCurveActions.SetWeight(source!, first,
                true, float.NaN), Is.False);
            second.InterpolationTypeOut = EVectorInterpType.Linear;
            Assert.That(InspectorFloatCurveActions.SetWeightedSide(source!, second,
                true, true), Is.False);
            Assert.That(InspectorFloatCurveActions.SetWeight(source!, second,
                true, 0.5f), Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void WeightedEditCallbackConflictKeepsExternalWeight()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe key = new(0.5f, 1f, 2f,
                EVectorInterpType.Hermite);
            key.WeightedMode = EKeyframeWeightedMode.Out;
            curve.Keyframes.Add(key);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            bool changed = false;
            curve.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                key.OutWeight = 0.9f;
            };
            Assert.That(InspectorFloatCurveActions.SetWeight(source!, key,
                true, 0.8f), Is.False);
            Assert.That(changed, Is.True);
            Assert.That(key.OutWeight, Is.EqualTo(0.9f));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(key.OutWeight, Is.EqualTo(0.9f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void UnweightedHandleAndSyncEditsUndoCompleteAuthoredState()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe key = new(12, 24, 1, 3, -2, 4,
                EVectorInterpType.Hermite);
            key.RestorePublishedHandles(1, 3, -2, 4,
                EVectorInterpType.Hermite, EVectorInterpType.Hermite,
                false, false, false);
            curve.Keyframes.Add(key);
            curve.Keyframes.Add(new FloatKeyframe(1.5f, 7, 0,
                EVectorInterpType.Hermite));
            curve.Seek(0.4f, false);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out string reason), Is.True, reason);
            Assert.That(key.TryCaptureAuthoredState(out var before), Is.True);
            float sampledBefore = curve.GetValue(1f);

            Assert.That(InspectorFloatCurveActions.SetIncomingValue(source!, key, 5), Is.True);
            Assert.That(InspectorFloatCurveActions.SetTangent(source!, key, false, -3), Is.True);
            Assert.That(InspectorFloatCurveActions.SetTangent(source!, key, true, 6), Is.True);
            Assert.That(InspectorFloatCurveActions.SetSyncValues(source!, key, true), Is.True);
            Assert.That(InspectorFloatCurveActions.SetSyncDirections(source!, key, true), Is.True);
            Assert.That(InspectorFloatCurveActions.SetSyncMagnitudes(source!, key, true), Is.True);
            Assert.That(key.TryCaptureAuthoredState(out var after), Is.True);
            Assert.That((after.SyncValues, after.SyncDirections, after.SyncMagnitudes),
                Is.EqualTo((true, true, true)));
            Assert.That((after.InValue, after.OutValue, after.InTangent,
                after.OutTangent), Is.EqualTo((4f, 4f, -4.5f, 4.5f)));
            Assert.That(curve.GetValue(1f), Is.Not.EqualTo(sampledBefore));
            Assert.That((key.Second, key.AuthoredFrameIndex, curve.CurrentTime),
                Is.EqualTo((0.5f, 12, 0.4f)));

            for (int index = 0; index < 6; index++)
                Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(key.TryCaptureAuthoredState(out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(before));
            for (int index = 0; index < 6; index++)
                Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(key.TryCaptureAuthoredState(out var redone), Is.True);
            Assert.That(redone, Is.EqualTo(after));
            Assert.That((key.Second, key.AuthoredFrameIndex, curve.CurrentTime),
                Is.EqualTo((0.5f, 12, 0.4f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void SyncRejectsNonFiniteComputedHandleBeforeEditingKey()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe key = new(0.5f, 1, 0,
                EVectorInterpType.Linear);
            key.RestorePublishedHandles(float.MaxValue, float.MaxValue, 0, 0,
                EVectorInterpType.Linear, EVectorInterpType.Linear,
                false, false, false);
            curve.Keyframes.Add(key);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(key.TryCaptureAuthoredState(out var before), Is.True);
            Assert.That(InspectorFloatCurveActions.SetSyncValues(source!, key, true),
                Is.False);
            Assert.That(key.TryCaptureAuthoredState(out var after), Is.True);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void InterpolationModesChangeSamplingAndPreserveExactKeyHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe first = new(0, 24, 0, 0, 0, 0,
                EVectorInterpType.Linear);
            FloatKeyframe second = new(24, 24, 1, 1, 0, 0,
                EVectorInterpType.Linear);
            curve.Keyframes.Add(first);
            curve.Keyframes.Add(second);
            curve.Seek(0.25f, false);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out string reason), Is.True, reason);
            Assert.That(first.TryCaptureAuthoredState(out var before), Is.True);
            float linear = curve.GetValue(0.5f);

            Assert.That(InspectorFloatCurveActions.SetInterpolation(source!, first,
                true, EVectorInterpType.Step), Is.True);
            Assert.That(InspectorFloatCurveActions.SetInterpolation(source!, first,
                false, EVectorInterpType.Hermite), Is.True);
            Assert.That(first.TryCaptureAuthoredState(out var changed), Is.True);
            Assert.That(changed, Is.EqualTo(before with
            {
                InterpolationIn = EVectorInterpType.Hermite,
                InterpolationOut = EVectorInterpType.Step
            }));
            Assert.That((first.Second, first.AuthoredFrameIndex, curve.CurrentTime),
                Is.EqualTo((0f, 0, 0.25f)));
            Assert.That(curve.GetValue(0.5f), Is.Not.EqualTo(linear));

            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.TryCaptureAuthoredState(out var restored), Is.True);
            Assert.That(restored, Is.EqualTo(before));
            Assert.That(curve.GetValue(0.5f), Is.EqualTo(linear).Within(0.0001f));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(first.TryCaptureAuthoredState(out var redone), Is.True);
            Assert.That(redone, Is.EqualTo(changed));
            Assert.That((first.Second, first.AuthoredFrameIndex, curve.CurrentTime),
                Is.EqualTo((0f, 0, 0.25f)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void InterpolationRejectsInvalidModeAndReplacedSource()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe key = new(0.5f, 1, 0,
                EVectorInterpType.Linear);
            curve.Keyframes.Add(key);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(InspectorFloatCurveActions.SetInterpolation(source!, key,
                true, (EVectorInterpType)999), Is.False);
            Assert.That(Undo.CanUndo, Is.False);
            owner.Curve = new PropAnimFloat(2, false, true);
            Assert.That(InspectorFloatCurveActions.SetInterpolation(source!, key,
                true, EVectorInterpType.Step), Is.False);
            Assert.That(key.InterpolationTypeOut, Is.EqualTo(EVectorInterpType.Linear));
        }
        finally { Undo.ClearHistory(); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void AuthoredEditCallbackChangingTimeOrFrameRejectsStaleHistory(
        bool editValue, bool changeFrame)
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe key = new(12, 24, 1, 0,
                EVectorInterpType.Linear);
            curve.Keyframes.Add(key);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            bool changed = false;
            curve.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                if (changeFrame) key.AuthoredFrameIndex = 13;
                else key.Second = 0.75f;
            };
            bool accepted = editValue
                ? InspectorFloatCurveActions.SetValue(source!, key, 2f)
                : InspectorFloatCurveActions.SetInterpolation(source!, key,
                    true, EVectorInterpType.Smooth);
            Assert.That(accepted, Is.False);
            Assert.That(changed, Is.True);
            Assert.That((key.Second, key.AuthoredFrameIndex),
                Is.EqualTo((changeFrame ? 0.5f : 0.75f, changeFrame ? 13 : -1)));
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That((key.Second, key.AuthoredFrameIndex),
                Is.EqualTo((changeFrame ? 0.5f : 0.75f, changeFrame ? 13 : -1)));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void GetterOnlyCurveSupportsConsecutiveExactEditsAndHistory()
    {
        Undo.ClearHistory();
        try
        {
            TransformKeyCollection owner = new();
            owner.SetLength(2, false);
            PropAnimFloat curve = owner.TranslationX;
            curve.Seek(0.4f, false);
            var property = typeof(TransformKeyCollection).GetProperty(
                nameof(TransformKeyCollection.TranslationX))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out string reason), Is.True, reason);

            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f,
                out FloatKeyframe? key), Is.True);
            Assert.That(key, Is.Not.Null);
            Assert.That(InspectorFloatCurveActions.SetValue(source!, key!, 3), Is.True);
            Assert.That(InspectorFloatCurveActions.SetTime(source!, key!, 1), Is.True);
            Assert.That(InspectorFloatCurveActions.SetLength(source!, 4, true), Is.True);
            Assert.That((key!.Second, key.OutValue, key.AuthoredFrameIndex),
                Is.EqualTo((2f, 3f, -1)));
            Assert.That(curve.CurrentTime, Is.EqualTo(0.4f));

            for (int index = 0; index < 4; index++)
                Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((curve.LengthInSeconds, curve.Keyframes.Count),
                Is.EqualTo((2f, 0)));
            for (int index = 0; index < 4; index++)
                Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((curve.LengthInSeconds, key.Second, key.OutValue),
                Is.EqualTo((4f, 2f, 3f)));
            Assert.That(curve.CurrentTime, Is.EqualTo(0.4f));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void SharedCurveEditsOnceAndDistinctCurvesReject()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat shared = new(2, false, true);
            InspectorFloatCurveTestOwner first = new() { Curve = shared };
            InspectorFloatCurveTestOwner second = new() { Curve = shared };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property,
                [first, second], out var source, out string reason), Is.True, reason);
            Assert.That(InspectorFloatCurveActions.Add(source!, 1,
                out _), Is.True);
            Assert.That(shared.Keyframes.Count, Is.EqualTo(1));
            FloatKeyframe sharedKey = shared.Keyframes.First!;
            Assert.That(InspectorFloatCurveActions.SetInterpolation(source!,
                sharedKey, true, EVectorInterpType.Step), Is.True);
            Assert.That(sharedKey.InterpolationTypeOut, Is.EqualTo(EVectorInterpType.Step));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(sharedKey.InterpolationTypeOut, Is.EqualTo(EVectorInterpType.Linear));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(shared.Keyframes.Count, Is.Zero);

            second.Curve = new PropAnimFloat(2, false, true);
            Assert.That(source!.IsCurrent(), Is.False);
            Assert.That(InspectorFloatCurveSource.TryCapture(property,
                [first, second], out _, out string distinctReason), Is.False);
            Assert.That(distinctReason, Is.EqualTo("Multiple curves are selected."));
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void ReplacedReferenceAndTrackRejectOldHistory()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f, out _), Is.True);
            owner.Curve = new PropAnimFloat(2, false, true);
            Assert.That(source!.IsCurrent(), Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(curve.Keyframes.Count, Is.EqualTo(1));
        }
        finally { Undo.ClearHistory(); }

        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f, out _), Is.True);
            curve.Keyframes = new KeyframeTrack<FloatKeyframe>();
            Assert.That(source!.IsCurrent(), Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void RemoveAndRestorePreserveFrameAndHandles()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            FloatKeyframe key = new(12, 24, 2, 4, 0, 0,
                EVectorInterpType.Smooth);
            curve.Keyframes.Add(key);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(InspectorFloatCurveActions.Remove(source!, key), Is.True);
            Assert.That(key.OwningTrack, Is.Null);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((key.Second, key.AuthoredFrameIndex, key.InValue,
                key.OutValue), Is.EqualTo((0.5f, 12, 2f, 4f)));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(curve.Keyframes.Count, Is.Zero);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void AddRejectsCallbackThatChangesInsertedTime()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            bool changed = false;
            curve.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                curve.Keyframes.First!.Second = 0.75f;
            };
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f,
                out FloatKeyframe? added), Is.False);
            Assert.That(added, Is.Null);
            Assert.That(changed, Is.True);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void DetachedKeyChangeRejectsRedo()
    {
        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            InspectorFloatCurveTestOwner owner = new() { Curve = curve };
            var property = typeof(InspectorFloatCurveTestOwner).GetProperty(
                nameof(InspectorFloatCurveTestOwner.Curve))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f,
                out FloatKeyframe? key), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            key!.OutValue = 8;
            Assert.Throws<InvalidOperationException>(() => Undo.TryRedo());
            Assert.That(curve.Keyframes.Count, Is.Zero);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void AttachedTransitionEditMarksItsStateMachineDocument()
    {
        Undo.ClearHistory();
        try
        {
            var (machine, _, _, transition, curve) = CreateAttached();
            machine.ClearDirty();
            curve.ClearDirty();
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
            Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine,
                0, 0, 0, false, out var path, out _), Is.True);
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
                out var source, out string reason, path), Is.True, reason);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f, out _), Is.True);
            Assert.That(machine.IsDirty, Is.True);
            Assert.That(curve.IsDirty, Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void TransitionMembershipChangeRejectsOldEditHistory()
    {
        Undo.ClearHistory();
        try
        {
            var (machine, layer, state, transition, _) = CreateAttached();
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
            Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine,
                0, 0, 0, false, out var path, out _), Is.True);
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
                out var source, out string reason, path), Is.True, reason);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f, out _), Is.True);
            state.Transitions = [transition];
            Assert.That(source!.IsCurrent(), Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(machine.Layers.Contains(layer), Is.True);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void TransitionTransferRejectsOldProofAndAnyStateUsesForwardPath()
    {
        var (machine, layer, _, transition, _) = CreateAttached();
        var property = typeof(AnimStateTransition).GetProperty(
            nameof(AnimStateTransition.CustomBlendFunction))!;
        Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
            out _, out string unavailable), Is.False);
        Assert.That(unavailable, Does.Contain("Layers detail"));
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine,
            0, 0, 0, false, out var path, out _), Is.True);
        Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
            out var source, out string reason, path), Is.True, reason);
        machine.Layers.Remove(layer);
        Assert.That(source!.IsCurrent(), Is.False);

        AnimStateTransition anyTransition = new()
        {
            CustomBlendFunction = new PropAnimFloat(2, false, true)
        };
        layer.AnyState.Transitions = [anyTransition];
        machine.Layers.Add(layer);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine,
            0, -1, 0, true, out var anyPath, out _), Is.True);
        Assert.That(InspectorFloatCurveSource.TryCapture(property, [anyTransition],
            out var anySource, out reason, anyPath), Is.True, reason);
        Assert.That(anySource!.IsCurrent(), Is.True);
    }

    private static (AnimStateMachine Machine, AnimLayer Layer, AnimState State,
        AnimStateTransition Transition, PropAnimFloat Curve) CreateAttached()
    {
        PropAnimFloat curve = new(2, false, true);
        AnimStateTransition transition = new() { CustomBlendFunction = curve };
        AnimState state = new();
        state.Transitions = [transition];
        AnimLayer layer = new(state);
        AnimStateMachine machine = new();
        machine.Layers.Add(layer);
        return (machine, layer, state, transition, curve);
    }
}
