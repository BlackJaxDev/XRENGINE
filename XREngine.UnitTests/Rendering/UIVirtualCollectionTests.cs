using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIVirtualCollectionTests
{
    [Test]
    public void OffscreenItemRequest_RealizesStableIdWithoutScanningMillionItems()
    {
        CountingVirtualListProvider provider = new(1_000_000);
        UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0, VirtualProvider = provider };
        list.SynchronizeVirtualRows(100);

        list.TryScrollToVirtualItem(900_000).ShouldBeTrue();
        list.SynchronizeVirtualRows(100);
        list.TryGetRealizedVirtualRow(900_000, out UIBoundableTransform? row,
            out UIVirtualRowLease lease).ShouldBeTrue();
        row.ShouldNotBeNull();
        lease.ItemId.ShouldBe(900_000UL);
        provider.Created.ShouldBeLessThanOrEqualTo(10);
        list.TryScrollToVirtualItem(1_000_001).ShouldBeFalse();

        provider.AdvanceRevision();
        list.TryGetRealizedVirtualRow(900_000, out _, out _).ShouldBeFalse();
        list.SynchronizeVirtualRows(100);
        list.TryGetRealizedVirtualRow(900_000, out _, out _).ShouldBeTrue();

        list.TryScrollToVirtualItem(1).ShouldBeTrue();
        list.SynchronizeVirtualRows(100);
        list.TryGetRealizedVirtualRow(900_000, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void MillionItemList_RealizesOnlyViewportRowsAndReusesPool()
    {
        CountingVirtualListProvider provider = new(1_000_000);
        UIListTransform list = new() { ItemSize = 20, VirtualProvider = provider };

        list.SynchronizeVirtualRows(100);
        list.VirtualContentExtent.ShouldBe(20_000_000f);
        list.VirtualFirstIndex.ShouldBe(0);
        list.VirtualEndIndex.ShouldBe(7);
        provider.Created.ShouldBe(7);

        list.ContentScrollOffset = 1_000;
        list.SynchronizeVirtualRows(100);
        list.VirtualFirstIndex.ShouldBe(48);
        list.VirtualEndIndex.ShouldBe(57);
        provider.Created.ShouldBe(9);
        provider.Unbound.ShouldBe(7);

        list.ContentScrollOffset = 2_000;
        list.SynchronizeVirtualRows(100);
        provider.Created.ShouldBe(9);
        list.ChildCount.ShouldBe(9);
    }

    [Test]
    public void Recycle_ReportsOldLeaseBeforeRowRebind()
    {
        CountingVirtualListProvider provider = new(100);
        UIListTransform list = new() { ItemSize = 10, VirtualProvider = provider, VirtualOverscan = 0 };
        list.SynchronizeVirtualRows(10);
        UIBoundableTransform row = (UIBoundableTransform)list.Children[0];
        list.TryGetVirtualRowLease(row, out UIVirtualRowLease oldLease).ShouldBeTrue();
        UIVirtualRowLease recycled = default;
        list.VirtualRowRecycling += (_, lease) => recycled = lease;

        list.ContentScrollOffset = 20;
        list.SynchronizeVirtualRows(10);

        recycled.ShouldBe(oldLease);
        list.TryGetVirtualRowLease(row, out UIVirtualRowLease newLease).ShouldBeTrue();
        newLease.ItemId.ShouldBe(3UL);
        newLease.Incarnation.ShouldNotBe(oldLease.Incarnation);
    }

    [Test]
    public void RevisionRefresh_KeepsLeaseForTheSameItem()
    {
        CountingVirtualListProvider provider = new(10);
        UIListTransform list = new() { ItemSize = 10, VirtualProvider = provider, VirtualOverscan = 0 };
        list.SynchronizeVirtualRows(10);
        UIBoundableTransform row = (UIBoundableTransform)list.Children[0];
        list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease).ShouldBeTrue();
        int recycled = 0;
        list.VirtualRowRecycling += (_, _) => recycled++;

        provider.AdvanceRevision();
        list.SynchronizeVirtualRows(10);

        list.TryGetVirtualRowLease(row, out UIVirtualRowLease refreshed).ShouldBeTrue();
        refreshed.ShouldBe(lease);
        provider.Bound.ShouldBe(2);
        provider.Unbound.ShouldBe(0);
        recycled.ShouldBe(0);
    }

    [Test]
    public void InsertBeforeVisibleItem_KeepsItsRowLease()
    {
        UIVirtualTreeProvider tree = new(
            () => new UIBoundableTransform(),
            (_, _, _, _) => { },
            (_, _, _) => { });
        tree.Insert(1, 0, 0, "First");
        tree.Insert(2, 0, 1, "Second");
        tree.Insert(3, 0, 2, "Third");
        UIListTransform list = new() { ItemSize = 10, VirtualOverscan = 0, VirtualProvider = tree };
        list.SynchronizeVirtualRows(30);
        UIBoundableTransform? originalRow = null;
        UIVirtualRowLease originalLease = default;
        foreach (var child in list.Children)
            if (child is UIBoundableTransform row &&
                list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) && lease.ItemId == 2)
            {
                originalRow = row;
                originalLease = lease;
            }
        originalRow.ShouldNotBeNull();

        tree.Insert(4, 0, 0, "Inserted");
        list.SynchronizeVirtualRows(30);

        list.TryGetVirtualRowLease(originalRow!, out UIVirtualRowLease movedLease).ShouldBeTrue();
        movedLease.ShouldBe(originalLease);
        list.TryGetVirtualRowIndex(originalRow!, out int newIndex).ShouldBeTrue();
        newIndex.ShouldBe(2);
    }

    [Test]
    public void TreeEdits_UpdateVisibleOrderWithoutRecreatingRows()
    {
        int created = 0;
        UIVirtualTreeProvider tree = new(
            () => { created++; return new UIBoundableTransform(); },
            (_, _, _, _) => { },
            (_, _, _) => { });
        tree.Insert(1, 0, 0, "A", true);
        tree.Insert(2, 1, 0, "B");
        tree.Insert(3, 1, 1, "C");
        tree.Insert(4, 0, 1, "D");
        tree.Count.ShouldBe(2);

        tree.SetExpanded(1, true).ShouldBeTrue();
        tree.Count.ShouldBe(4);
        tree.GetItemId(0).ShouldBe(1UL);
        tree.GetItemId(2).ShouldBe(3UL);
        tree.TryGetIndexOfItemId(3, out int index).ShouldBeTrue();
        index.ShouldBe(2);
        tree.Move(3, 0, 1).ShouldBeTrue();
        tree.GetItemId(2).ShouldBe(3UL);
        tree.TryGetIndexOfItemId(3, out index).ShouldBeTrue();
        index.ShouldBe(2);
        tree.Rename(3, "Moved").ShouldBeTrue();
        tree.SetSelected(3, true).ShouldBeTrue();
        tree.Activate(3).ShouldBeTrue();
        tree.IsSelected(3).ShouldBeTrue();
        tree.ActiveId.ShouldBe(3UL);
        tree.Remove(1).ShouldBeTrue();
        tree.Count.ShouldBe(2);
        tree.GetItemId(0).ShouldBe(3UL);
        tree.TryGetIndexOfItemId(1, out _).ShouldBeFalse();
        tree.TryGetIndexOfItemId(3, out index).ShouldBeTrue();
        index.ShouldBe(0);
        created.ShouldBe(0);
    }

    [Test]
    public void GridPlacement_UsesArrangedOffsetAndRejectsInvalidSpan()
    {
        UIGridTransform.UIGridChildPlacementInfo placement = new(new UITransform())
        {
            Row = 2,
            Column = 1,
            RowSpan = 3,
            ColumnSpan = 2,
            Offset = new Vector2(11, 17)
        };

        placement.GetRelativeItemMatrix().Translation.ShouldBe(new Vector3(11, 17, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => placement.RowSpan = 0);
        Should.Throw<ArgumentOutOfRangeException>(() => placement.Column = -1);
    }
}
