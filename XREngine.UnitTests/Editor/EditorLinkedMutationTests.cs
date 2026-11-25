using System.Collections.Generic;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class EditorLinkedMutationTests
{
    [SetUp]
    public void Clear() => Undo.ClearHistory();

    [TearDown]
    public void ClearAfter() => Undo.ClearHistory();

    [Test]
    public void RemoveAndReplayKeepTheExactNode()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1, 1, 2]) };
        LinkedListNode<int> node = target.Values.First!.Next!;
        EditorLinkedSourceLease source = Capture(target);

        EditorLinkedMutation.Remove([source], 1, null);
        Assert.That(node.List, Is.Null);
        Assert.That(target.Values, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values.First!.Next, Is.SameAs(node));
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(node.List, Is.Null);
    }

    [Test]
    public void AddLastReplaysWithTheSameNode()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1]) };
        EditorLinkedMutation.AddLast([Capture(target)], null);
        LinkedListNode<int> added = target.Values.Last!;
        Assert.That(added.Value, Is.Zero);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(added.List, Is.Null);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(target.Values.Last, Is.SameAs(added));
    }

    [Test]
    public void AliasedOwnersMutateOneListOnce()
    {
        LinkedList<int> shared = new([1]);
        EditorLinkedMutationTestTarget first = new() { Values = shared };
        EditorLinkedMutationTestTarget second = new() { Values = shared };
        EditorLinkedMutation.AddLast([Capture(first), Capture(second)], null);
        Assert.That(shared.Count, Is.EqualTo(2));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(shared.Count, Is.EqualTo(1));
    }

    [Test]
    public void MultiOwnerSetIsOneUndoStep()
    {
        EditorLinkedMutationTestTarget first = new() { Values = new([1, 2]) };
        EditorLinkedMutationTestTarget second = new() { Values = new([3, 4]) };
        EditorLinkedElementBindingTestDouble[] bindings =
        [
            new(Capture(first), 1), new(Capture(second), 1)
        ];

        EditorLinkedMutation.Set(bindings, _ => 9, null);
        Assert.That(first.Values.Last!.Value, Is.EqualTo(9));
        Assert.That(second.Values.Last!.Value, Is.EqualTo(9));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(first.Values.Last!.Value, Is.EqualTo(2));
        Assert.That(second.Values.Last!.Value, Is.EqualTo(4));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void ForeignNodeEditBlocksReplayAndKeepsHistory()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1, 2]) };
        EditorLinkedMutation.Remove([Capture(target)], 1, null);
        target.Values.First!.Value = 7;
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(Undo.CanUndo, Is.True);
        Assert.That(target.Values.First!.Value, Is.EqualTo(7));
    }

    [Test]
    public void DetachedNodeReattachmentBlocksUndo()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1, 2]) };
        LinkedListNode<int> removed = target.Values.Last!;
        EditorLinkedMutation.Remove([Capture(target)], 1, null);
        LinkedList<int> foreign = new();
        foreign.AddLast(removed);
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(removed.List, Is.SameAs(foreign));
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void DetachedNodeValueChangeBlocksUndo()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1, 2]) };
        LinkedListNode<int> removed = target.Values.Last!;
        EditorLinkedMutation.Remove([Capture(target)], 1, null);
        removed.Value = 19;
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(removed.List, Is.Null);
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void OldVisibleSourceCannotEditAReplacement()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1]) };
        EditorLinkedSourceLease source = Capture(target);
        target.Values = new([8]);
        Assert.Throws<InvalidOperationException>(() => EditorLinkedMutation.AddLast([source], null));
        Assert.That(target.Values, Is.EqualTo(new[] { 8 }));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void ReplacedOwnerListBlocksUndoWithoutTouchingEitherList()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1]) };
        LinkedList<int> old = target.Values;
        EditorLinkedMutation.AddLast([Capture(target)], null);
        target.Values = new([8]);
        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.That(old, Is.EqualTo(new[] { 1, 0 }));
        Assert.That(target.Values, Is.EqualTo(new[] { 8 }));
        Assert.That(Undo.CanUndo, Is.True);
    }

    [Test]
    public void CustomGetterIsRejectedBeforeItRuns()
    {
        EditorLinkedCustomGetterTarget target = new();
        var property = typeof(EditorLinkedCustomGetterTarget).GetProperty(nameof(target.Values))!;
        Assert.Throws<InvalidOperationException>(() => EditorLinkedMutation.Capture(target,
            property, () => true));
        Assert.That(target.GetterCalls, Is.Zero);
    }

    [Test]
    public void ValueFactoryCannotAdoptAnotherTargetChange()
    {
        EditorLinkedMutationTestTarget first = new() { Values = new([1]) };
        EditorLinkedMutationTestTarget second = new() { Values = new([2]) };
        EditorLinkedElementBindingTestDouble[] bindings =
        [
            new(Capture(first), 0), new(Capture(second), 0)
        ];
        Assert.Throws<InvalidOperationException>(() => EditorLinkedMutation.Set(bindings, old =>
        {
            if ((int)old! == 1) second.Values.First!.Value = 6;
            return 9;
        }, null));
        Assert.That(first.Values.First!.Value, Is.EqualTo(1));
        Assert.That(second.Values.First!.Value, Is.EqualTo(6));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void StaleDisplayedBindingCannotEditItsNode()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1]) };
        EditorLinkedElementBindingTestDouble binding = new(Capture(target), 0)
        {
            DisplayedValue = 40
        };
        Assert.Throws<InvalidOperationException>(() => EditorLinkedMutation.Set([binding], _ => 8, null));
        Assert.That(target.Values.First!.Value, Is.EqualTo(1));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void ThrowingBindingNotificationKeepsCommittedUndo()
    {
        EditorLinkedMutationTestTarget target = new() { Values = new([1]) };
        EditorLinkedElementBindingTestDouble binding = new(Capture(target), 0)
        {
            OnAccept = () => throw new InvalidOperationException("view closed")
        };
        EditorLinkedMutation.Set([binding], _ => 8, null);
        Assert.That(target.Values.First!.Value, Is.EqualTo(8));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(target.Values.First!.Value, Is.EqualTo(1));
    }

    private static EditorLinkedSourceLease Capture(EditorLinkedMutationTestTarget target)
        => EditorLinkedMutation.Capture(target,
            typeof(EditorLinkedMutationTestTarget).GetProperty(nameof(target.Values))!, () => true);
}
