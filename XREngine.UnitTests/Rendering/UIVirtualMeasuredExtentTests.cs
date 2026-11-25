using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIVirtualMeasuredExtentTests
{
    [Test]
    public void MeasuredExtents_SetContentExtentRangeAndPlacement()
    {
        MeasuredVirtualListProvider provider = new([10, 50, 20, 30, 40]);
        UIListTransform list = new() { ItemSpacing = 2, VirtualOverscan = 0, VirtualProvider = provider };

        list.SynchronizeVirtualRows(30);

        list.VirtualContentExtent.ShouldBe(158f);
        list.VirtualFirstIndex.ShouldBe(0);
        list.VirtualEndIndex.ShouldBe(2);
        Placement(list, 2UL).ShouldBe((12f, 50f));

        list.ContentScrollOffset = 70;
        list.SynchronizeVirtualRows(30);

        list.VirtualFirstIndex.ShouldBe(2);
        list.VirtualEndIndex.ShouldBe(4);
        Placement(list, 3UL).ShouldBe((64f, 20f));
        Placement(list, 4UL).ShouldBe((86f, 30f));
    }

    [Test]
    public void ExtentRevision_RereadsExtentsAndMovesRealizedRows()
    {
        MeasuredVirtualListProvider provider = new([10, 10, 10]);
        UIListTransform list = new() { VirtualOverscan = 0, VirtualProvider = provider };
        list.SynchronizeVirtualRows(100);
        int reads = provider.ExtentReads;

        list.SynchronizeVirtualRows(100);
        provider.ExtentReads.ShouldBe(reads, "An unchanged revision must not read extents again.");

        provider.SetExtent(0, 40);
        list.SynchronizeVirtualRows(100);

        provider.ExtentReads.ShouldBe(reads + 3);
        list.VirtualContentExtent.ShouldBe(60f);
        Placement(list, 2UL).ShouldBe((40f, 10f));
    }

    [Test]
    public void ScrollToItem_UsesMeasuredStartAndExtent()
    {
        MeasuredVirtualListProvider provider = new([100, 100, 5, 100, 100]);
        UIListTransform list = new() { VirtualOverscan = 0, VirtualProvider = provider };
        list.SynchronizeVirtualRows(50);

        list.TryScrollToVirtualItem(4).ShouldBeTrue();
        list.ContentScrollOffset.ShouldBe(255f);

        list.TryScrollToVirtualItem(3, center: true).ShouldBeTrue();
        list.ContentScrollOffset.ShouldBe(177.5f);
    }

    [Test]
    public void InvalidExtent_IsRejected()
    {
        MeasuredVirtualListProvider provider = new([10, float.NaN]);
        UIListTransform list = new() { VirtualProvider = provider };

        Should.Throw<InvalidOperationException>(() => list.SynchronizeVirtualRows(100));
        provider.SetExtent(1, -1);
        Should.Throw<InvalidOperationException>(() => list.SynchronizeVirtualRows(100));
    }

    [Test]
    public void DisabledExtents_RequireFixedItemSize()
    {
        MeasuredVirtualListProvider provider = new([10, 10]) { HasItemExtents = false };
        UIListTransform list = new() { VirtualProvider = provider };

        Should.Throw<InvalidOperationException>(() => list.SynchronizeVirtualRows(100));
        list.ItemSize = 25;
        list.SynchronizeVirtualRows(100);
        list.VirtualContentExtent.ShouldBe(50f);
        Placement(list, 2UL).ShouldBe((25f, 25f));
        provider.ExtentReads.ShouldBe(0);
    }

    private static (float Start, float Extent) Placement(UIListTransform list, ulong itemId)
    {
        list.TryGetRealizedVirtualRow(itemId, out UIBoundableTransform? row, out _).ShouldBeTrue();
        list.TryGetVirtualRowPlacement(row!, out _, out float start, out float extent).ShouldBeTrue();
        return (start, extent);
    }
}
