using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class InspectorFloatCurveActionsTests
{
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
            AnimStateTransition first = new() { CustomBlendFunction = shared };
            AnimStateTransition second = new() { CustomBlendFunction = shared };
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property,
                [first, second], out var source, out string reason), Is.True, reason);
            Assert.That(InspectorFloatCurveActions.Add(source!, 1,
                out _), Is.True);
            Assert.That(shared.Keyframes.Count, Is.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(shared.Keyframes.Count, Is.Zero);

            second.CustomBlendFunction = new PropAnimFloat(2, false, true);
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
            AnimStateTransition owner = new() { CustomBlendFunction = curve };
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [owner],
                out var source, out _), Is.True);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f, out _), Is.True);
            owner.CustomBlendFunction = new PropAnimFloat(2, false, true);
            Assert.That(source!.IsCurrent(), Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(curve.Keyframes.Count, Is.EqualTo(1));
        }
        finally { Undo.ClearHistory(); }

        Undo.ClearHistory();
        try
        {
            PropAnimFloat curve = new(2, false, true);
            AnimStateTransition owner = new() { CustomBlendFunction = curve };
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
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
            AnimStateTransition owner = new() { CustomBlendFunction = curve };
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
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
            AnimStateTransition owner = new() { CustomBlendFunction = curve };
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
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
            AnimStateTransition owner = new() { CustomBlendFunction = curve };
            var property = typeof(AnimStateTransition).GetProperty(
                nameof(AnimStateTransition.CustomBlendFunction))!;
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
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
                out var source, out string reason), Is.True, reason);
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
            Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
                out var source, out string reason), Is.True, reason);
            Assert.That(InspectorFloatCurveActions.Add(source!, 0.5f, out _), Is.True);
            state.Transitions = [transition];
            Assert.That(source!.IsCurrent(), Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(machine.Layers.Contains(layer), Is.True);
        }
        finally { Undo.ClearHistory(); }
    }

    [Test]
    public void TransitionTransferAndUnsupportedAnyStateRejectCapture()
    {
        var (machine, layer, _, transition, _) = CreateAttached();
        var property = typeof(AnimStateTransition).GetProperty(
            nameof(AnimStateTransition.CustomBlendFunction))!;
        Assert.That(InspectorFloatCurveSource.TryCapture(property, [transition],
            out var source, out string reason), Is.True, reason);
        machine.Layers.Remove(layer);
        Assert.That(source!.IsCurrent(), Is.False);

        AnimStateTransition anyTransition = new()
        {
            CustomBlendFunction = new PropAnimFloat(2, false, true)
        };
        layer.AnyState.Transitions = [anyTransition];
        Assert.That(InspectorFloatCurveSource.TryCapture(property, [anyTransition],
            out _, out string unavailable), Is.False);
        Assert.That(unavailable, Does.Contain("incomplete"));
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
        layer.Initialize(machine, null);
        return (machine, layer, state, transition, curve);
    }
}
