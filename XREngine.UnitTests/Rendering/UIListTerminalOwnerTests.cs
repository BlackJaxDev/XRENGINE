using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class UIListTerminalOwnerTests
{
    [Test]
    public void TerminalDetach_RetainsChildrenButCancelsInteractionsAndOldLeases()
    {
        SceneNode owner = new("Virtual list");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        list.ItemSize = 10;
        list.VirtualOverscan = 0;
        int unbound = 0;
        UIVirtualRowLease released = default;
        UIVirtualTreeProvider provider = new(
            () =>
            {
                SceneNode rowNode = new("Row");
                UIBoundableTransform row = rowNode.SetTransform<UIBoundableTransform>();
                rowNode.AddComponent<UIButtonComponent>();
                return row;
            },
            (_, _, _, _) => { },
            (_, id, incarnation) =>
            {
                Assert.That(id, Is.EqualTo(7UL));
                Assert.That(incarnation, Is.EqualTo(released.Incarnation));
                unbound++;
            });
        provider.Insert(7, 0, 0, "Seven");
        list.VirtualProvider = provider;
        list.SynchronizeVirtualRows(10);
        UIBoundableTransform row = (UIBoundableTransform)list.Children[0];
        UIButtonComponent button = row.SceneNode!.GetComponent<UIButtonComponent>()!;
        button.IsFocused = true;
        button.IsCaptured = true;
        button.IsMouseOver = true;
        long oldRevision = button.InteractionRevision;
        list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease);
        list.VirtualRowRecycling += (_, oldLease) => released = oldLease;
        try
        {
            list.DetachVirtualProviderForOwnerTeardown();
            Assert.Multiple(() =>
            {
                Assert.That(list.VirtualProvider, Is.Null);
                Assert.That(list.ChildCount, Is.EqualTo(1));
                Assert.That(row.Parent, Is.SameAs(list));
                Assert.That(row.Visibility, Is.EqualTo(EVisibility.Hidden));
                Assert.That(list.TryGetVirtualRowLease(row, out _), Is.False);
                Assert.That(list.TryGetRealizedVirtualRow(7, out _, out _), Is.False);
                Assert.That(list.VirtualItemCount, Is.Zero);
                Assert.That(unbound, Is.EqualTo(1));
                Assert.That(released, Is.EqualTo(lease));
                Assert.That(button.IsFocused || button.IsCaptured || button.IsMouseOver, Is.False);
                Assert.That(button.InteractionRevision, Is.GreaterThan(oldRevision));
            });
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void TerminalDetach_ContinuesAfterThrowAndPreservesReentrantReplacement()
    {
        SceneNode owner = new("Virtual list");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        list.ItemSize = 10;
        list.VirtualOverscan = 0;
        List<ulong> unbound = [];
        CountingVirtualListProvider replacement = new(1);
        UIVirtualTreeProvider original = new(
            () => new UIBoundableTransform(),
            (_, _, _, _) => { },
            (_, id, _) =>
            {
                unbound.Add(id);
                if (id == 1)
                    throw new InvalidOperationException("Unbind failed");
            });
        original.Insert(1, 0, 0, "One");
        original.Insert(2, 0, 1, "Two");
        list.VirtualProvider = original;
        list.SynchronizeVirtualRows(20);
        UIBoundableTransform[] oldRows = [.. list.Children.Cast<UIBoundableTransform>()];
        list.VirtualRowRecycling += (_, _) => throw new InvalidOperationException("Recycle failed");
        list.VirtualRowRecycling += (_, _) => list.VirtualProvider ??= replacement;
        try
        {
            AggregateException error = Assert.Throws<AggregateException>(
                list.DetachVirtualProviderForOwnerTeardown)!;
            Assert.Multiple(() =>
            {
                Assert.That(error.InnerExceptions.Count, Is.GreaterThanOrEqualTo(3));
                Assert.That(unbound, Is.EquivalentTo(new ulong[] { 1, 2 }));
                Assert.That(list.VirtualProvider, Is.SameAs(replacement));
                Assert.That(list.ChildCount, Is.EqualTo(2));
                Assert.That(oldRows.All(row => ReferenceEquals(row.Parent, list) &&
                    row.Visibility == EVisibility.Hidden && !list.TryGetVirtualRowLease(row, out _)), Is.True);
            });
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void TerminalDetach_RejectsSameRowReuseUntilOldUnbindFinishes()
    {
        SceneNode owner = new("Virtual list");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        list.ItemSize = 10;
        list.VirtualOverscan = 0;
        UIBoundableTransform sharedRow = new();
        ulong boundId = 0;
        int oldUnbinds = 0;
        UIVirtualTreeProvider original = new(
            () => sharedRow,
            (_, item, _, _) => boundId = item.Id,
            (_, _, _) => { oldUnbinds++; boundId = 0; });
        UIVirtualTreeProvider replacement = new(
            () => sharedRow,
            (_, item, _, _) => boundId = item.Id,
            (_, _, _) => boundId = 0);
        original.Insert(1, 0, 0, "Old");
        replacement.Insert(2, 0, 0, "Replacement");
        list.VirtualProvider = original;
        list.SynchronizeVirtualRows(10);
        bool reuseRejected = false;
        list.VirtualRowRecycling += (_, _) =>
        {
            list.VirtualProvider = replacement;
            reuseRejected = Assert.Throws<InvalidOperationException>(
                () => list.SynchronizeVirtualRows(10)) is not null;
            Assert.That(boundId, Is.EqualTo(1UL), "The replacement must not bind before old unbind.");
        };
        try
        {
            list.DetachVirtualProviderForOwnerTeardown();
            Assert.Multiple(() =>
            {
                Assert.That(reuseRejected, Is.True);
                Assert.That(oldUnbinds, Is.EqualTo(1));
                Assert.That(boundId, Is.Zero);
                Assert.That(list.VirtualProvider, Is.SameAs(replacement));
                Assert.That(sharedRow.Parent, Is.SameAs(list));
            });
            list.SynchronizeVirtualRows(10);
            Assert.Multiple(() =>
            {
                Assert.That(boundId, Is.EqualTo(2UL));
                Assert.That(list.TryGetVirtualRowLease(sharedRow, out UIVirtualRowLease lease), Is.True);
                Assert.That(lease.ItemId, Is.EqualTo(2UL));
            });
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void TerminalDetach_NestedDistinctOwnerRetiresBothWithoutDroppingOldReservation()
    {
        SceneNode owner = new("Virtual list");
        UIListTransform list = owner.SetTransform<UIListTransform>();
        list.ItemSize = 10;
        list.VirtualOverscan = 0;
        UIBoundableTransform oldRow = new();
        UIBoundableTransform newRow = new();
        int oldUnbound = 0;
        int newUnbound = 0;
        UIVirtualTreeProvider original = new(() => oldRow, (_, _, _, _) => { },
            (_, _, _) => oldUnbound++);
        UIVirtualTreeProvider replacement = new(() => newRow, (_, _, _, _) => { },
            (_, _, _) => newUnbound++);
        original.Insert(1, 0, 0, "Old");
        replacement.Insert(2, 0, 0, "New");
        list.VirtualProvider = original;
        list.SynchronizeVirtualRows(10);
        list.VirtualRowRecycling += (_, _) =>
        {
            list.VirtualProvider = replacement;
            list.SynchronizeVirtualRows(10);
            list.DetachVirtualProviderForOwnerTeardown();
        };
        try
        {
            Assert.DoesNotThrow(list.DetachVirtualProviderForOwnerTeardown);
            Assert.Multiple(() =>
            {
                Assert.That(oldUnbound, Is.EqualTo(1));
                Assert.That(newUnbound, Is.EqualTo(1));
                Assert.That(list.VirtualProvider, Is.Null);
                Assert.That(list.ChildCount, Is.EqualTo(2));
                Assert.That(oldRow.Parent, Is.SameAs(list));
                Assert.That(newRow.Parent, Is.SameAs(list));
                Assert.That(list.TryGetVirtualRowLease(oldRow, out _), Is.False);
                Assert.That(list.TryGetVirtualRowLease(newRow, out _), Is.False);
            });
        }
        finally { owner.Destroy(true); }
    }

    [Test]
    public void TerminalDetach_RejectsCrossListReuseUntilOldUnbindFinishes()
    {
        SceneNode oldOwner = new("Old list");
        SceneNode newOwner = new("New list");
        UIListTransform oldList = oldOwner.SetTransform<UIListTransform>();
        UIListTransform newList = newOwner.SetTransform<UIListTransform>();
        oldList.ItemSize = newList.ItemSize = 10;
        oldList.VirtualOverscan = newList.VirtualOverscan = 0;
        UIBoundableTransform sharedRow = new();
        ulong boundId = 0;
        int oldUnbound = 0;
        UIVirtualTreeProvider original = new(() => sharedRow,
            (_, item, _, _) => boundId = item.Id,
            (_, _, _) => { oldUnbound++; boundId = 0; });
        UIVirtualTreeProvider replacement = new(() => sharedRow,
            (_, item, _, _) => boundId = item.Id,
            (_, _, _) => boundId = 0);
        original.Insert(1, 0, 0, "Old");
        replacement.Insert(2, 0, 0, "New");
        oldList.VirtualProvider = original;
        oldList.SynchronizeVirtualRows(10);
        bool rejected = false;
        oldList.VirtualRowRecycling += (_, _) =>
        {
            newList.VirtualProvider = replacement;
            rejected = Assert.Throws<InvalidOperationException>(
                () => newList.SynchronizeVirtualRows(10)) is not null;
            Assert.That(sharedRow.Parent, Is.SameAs(oldList));
            Assert.That(boundId, Is.EqualTo(1UL));
        };
        try
        {
            oldList.DetachVirtualProviderForOwnerTeardown();
            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.True);
                Assert.That(oldUnbound, Is.EqualTo(1));
                Assert.That(boundId, Is.Zero);
                Assert.That(sharedRow.Parent, Is.SameAs(oldList));
            });
            newList.SynchronizeVirtualRows(10);
            Assert.Multiple(() =>
            {
                Assert.That(boundId, Is.EqualTo(2UL));
                Assert.That(sharedRow.Parent, Is.SameAs(newList));
                Assert.That(newList.TryGetVirtualRowLease(sharedRow, out UIVirtualRowLease lease), Is.True);
                Assert.That(lease.ItemId, Is.EqualTo(2UL));
            });
        }
        finally
        {
            newOwner.Destroy(true);
            oldOwner.Destroy(true);
        }
    }
}
