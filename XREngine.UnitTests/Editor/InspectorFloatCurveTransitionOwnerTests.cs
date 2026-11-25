using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class InspectorFloatCurveTransitionOwnerTests
{
    [Test]
    public void ForwardPathWorksBeforeAndAfterRuntimeInitialization()
    {
        var (machine, layer, _, transition) = CreateGraph();
        Assert.That(transition.Owner, Is.SameAs(machine.Layers[0].States[0]));
        Assert.That(layer.OwningStateMachine, Is.Null);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0, 0,
            false, out var ownership, out string reason), Is.True, reason);
        Assert.That(ownership!.Document, Is.SameAs(machine.SourceAsset));
        Assert.That(ownership.IsCurrent(), Is.True);

        layer.Initialize(machine, null);
        Assert.That(ownership.IsCurrent(), Is.True);
        layer.Deinitialize();
        Assert.That(ownership.IsCurrent(), Is.True);
    }

    [Test]
    public void DefaultListsAdmitForwardPathWithoutRuntimeBackrefs()
    {
        AnimStateTransition transition = new();
        AnimState state = new();
        state.Transitions.Add(transition);
        AnimLayer layer = new();
        layer.States.Add(state);
        AnimStateMachine machine = new();
        machine.Layers.Add(layer);
        Assert.That(transition.Owner, Is.Null);
        Assert.That(state.OwningLayer, Is.Null);
        Assert.That(layer.OwningStateMachine, Is.Null);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0,
            0, false, out var ownership, out string reason), Is.True, reason);
        Assert.That(ownership!.IsCurrent(), Is.True);

        AnimStateTransition anyTransition = new();
        layer.AnyState.Transitions.Add(anyTransition);
        Assert.That(anyTransition.Owner, Is.Null);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0,
            -1, 0, true, out var anyProof, out reason), Is.True, reason);
        Assert.That(anyProof!.IsCurrent(), Is.True);
    }

    [Test]
    public void ForwardPathRejectsReplacedOrRemovedContainers()
    {
        var (machine, layer, state, transition) = CreateGraph();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0, 0,
            false, out var ownership, out string reason), Is.True, reason);
        state.Transitions = [transition];
        Assert.That(ownership!.IsCurrent(), Is.False);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0, 0,
            false, out var replacement, out reason), Is.True, reason);
        machine.Layers.Remove(layer);
        Assert.That(replacement!.IsCurrent(), Is.False);
    }

    [Test]
    public void AnyStatePathUsesTheSelectedLayerWithoutRuntimeBackrefs()
    {
        AnimLayer layer = new();
        AnimStateTransition transition = new();
        layer.AnyState.Transitions = [transition];
        AnimStateMachine machine = new();
        machine.Layers.Add(layer);
        Assert.That(layer.AnyState.OwningLayer, Is.Null);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0,
            -1, 0, true, out var ownership, out string reason), Is.True, reason);
        Assert.That(ownership!.Transition, Is.SameAs(transition));
        Assert.That(ownership.IsCurrent(), Is.True);
        layer.AnyState.Transitions = [transition];
        Assert.That(ownership.IsCurrent(), Is.False);
    }

    [Test]
    public void RegeneratedListIdentityRejectsRetainedPath()
    {
        var (machine, _, _, _) = CreateGraph();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0, 0,
            false, out var ownership, out string reason), Is.True, reason);
        var layers = machine.Layers;
        layers.Generate();
        Assert.That(ownership!.IsCurrentFast(), Is.False);
    }

    [Test]
    public void ReorderedAndDuplicateMembershipRejectOldPaths()
    {
        var (machine, layer, state, transition) = CreateGraph();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0,
            0, false, out var first, out string reason), Is.True, reason);
        machine.Layers.Insert(0, new AnimLayer());
        Assert.That(first!.IsCurrent(), Is.False);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 1, 0,
            0, false, out var second, out reason), Is.True, reason);
        layer.States.Insert(0, new AnimState());
        Assert.That(second!.IsCurrent(), Is.False);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 1, 1,
            0, false, out var third, out reason), Is.True, reason);
        state.Transitions.Insert(0, new AnimStateTransition());
        Assert.That(third!.IsCurrent(), Is.False);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 1, 1,
            1, false, out var fourth, out reason), Is.True, reason);
        Assert.That(fourth!.Transition, Is.SameAs(transition));
        machine.Layers.Add(layer);
        Assert.That(fourth.IsCurrent(), Is.False);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 1, 1,
            1, false, out _, out _), Is.False);

        var (stateMachine, stateLayer, stateNode, _) = CreateGraph();
        stateLayer.States.Add(stateNode);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(stateMachine,
            0, 0, 0, false, out _, out _), Is.False);

        var (transitionMachine, _, transitionState, transitionNode) =
            CreateGraph();
        transitionState.Transitions.Add(transitionNode);
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(
            transitionMachine, 0, 0, 0, false, out _, out _), Is.False);
    }

    [Test]
    public void SourceDocumentReplacementRejectsRetainedPath()
    {
        var (machine, _, _, _) = CreateGraph();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0,
            0, false, out var ownership, out string reason), Is.True, reason);
        machine.SourceAsset = new AnimStateMachine();
        Assert.That(ownership!.IsCurrent(), Is.False);
    }

    [Test]
    public void WarmForwardCheckDoesNotAllocate()
    {
        var (machine, _, _, _) = CreateGraph();
        Assert.That(InspectorFloatTransitionOwnership.TryCapture(machine, 0, 0, 0,
            false, out var ownership, out _), Is.True);
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
        AnimState State, AnimStateTransition Transition) CreateGraph()
    {
        AnimStateTransition transition = new();
        AnimState state = new() { Transitions = [transition] };
        AnimLayer layer = new(state);
        AnimStateMachine machine = new();
        machine.Layers.Add(layer);
        return (machine, layer, state, transition);
    }
}
