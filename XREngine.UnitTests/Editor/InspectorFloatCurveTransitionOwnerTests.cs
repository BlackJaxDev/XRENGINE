using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class InspectorFloatCurveTransitionOwnerTests
{
    [Test]
    public void AttachedPathRetainsExactDocumentAndMembership()
    {
        var (machine, layer, state, transition) = CreateAttached();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(transition,
            out var ownership, out string reason), Is.True, reason);
        Assert.That(ownership!.Document, Is.SameAs(machine.SourceAsset));
        Assert.That(ownership.IsCurrent(), Is.True);

        state.Transitions = [transition];
        Assert.That(ownership.IsCurrent(), Is.False);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(transition,
            out var replacement, out reason), Is.True, reason);
        machine.Layers.Remove(layer);
        Assert.That(replacement!.IsCurrent(), Is.False);
    }

    [Test]
    public void DetachedPathRejectsLaterAttachment()
    {
        AnimStateTransition transition = new();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(transition,
            out var ownership, out string reason), Is.True, reason);
        Assert.That(ownership!.Document, Is.Null);
        AnimState state = new();
        state.Transitions = [transition];
        Assert.That(ownership.IsCurrent(), Is.False);
    }

    [Test]
    public void AnyStatePathWithoutOwningLayerIsUnavailable()
    {
        AnimLayer layer = new();
        AnimStateTransition transition = new();
        layer.AnyState.Transitions = [transition];
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(transition,
            out _, out string reason), Is.False);
        Assert.That(reason, Does.Contain("incomplete"));
    }

    [Test]
    public void WarmOwnershipCheckDoesNotAllocate()
    {
        var (_, _, _, transition) = CreateAttached();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(transition,
            out var ownership, out _), Is.True);
        for (int index = 0; index < 64; index++)
            _ = ownership!.IsCurrentFast();
        bool current = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1024; index++)
            current &= ownership!.IsCurrentFast();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(current, Is.True);
        Assert.That(bytes, Is.Zero);
    }

    private static (AnimStateMachine Machine, AnimLayer Layer,
        AnimState State, AnimStateTransition Transition) CreateAttached()
    {
        AnimStateTransition transition = new();
        AnimState state = new();
        state.Transitions = [transition];
        AnimLayer layer = new(state);
        AnimStateMachine machine = new();
        machine.Layers.Add(layer);
        layer.Initialize(machine, null);
        return (machine, layer, state, transition);
    }
}
