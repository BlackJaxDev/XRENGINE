using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CpuSpatialRenderTreeTests
{
    [Test]
    public void OctreeOriginCrossingItemsStayAtRoot()
    {
        var tree = new Octree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var items = CreateOriginCrossingItems(32);

        tree.AddRange(items);
        SwapSeveral(tree);

        SpatialTreeOccupancyStats stats = tree.GetOccupancyStats();
        stats.ItemCount.ShouldBe(items.Count);
        stats.RootItemCount.ShouldBe(items.Count);
        stats.MaxNodeItemCount.ShouldBe(items.Count);
    }

    [Test]
    public void CpuBvhSplitsOriginCrossingItemsIntoLeaves()
    {
        var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var items = CreateOriginCrossingItems(32);

        tree.AddRange(items);
        SwapSeveral(tree);

        SpatialTreeOccupancyStats stats = tree.GetOccupancyStats();
        stats.ItemCount.ShouldBe(items.Count);
        stats.RootItemCount.ShouldBe(0);
        stats.MaxNodeItemCount.ShouldBeLessThanOrEqualTo(8);
        stats.NodeCount.ShouldBeGreaterThan(1);

        int collected = 0;
        tree.CollectVisible(
            null,
            false,
            _ => collected++,
            static (item, cullingVolume, containsOnly) => item.ShouldRender);

        collected.ShouldBe(items.Count);
    }

    private static List<TestRenderItem> CreateOriginCrossingItems(int count)
    {
        var items = new List<TestRenderItem>(count);
        for (int i = 0; i < count; i++)
        {
            float x = -16.0f + i;
            items.Add(new TestRenderItem
            {
                LocalCullingVolume = AABB.FromCenterSize(new Vector3(x, 0.0f, 0.0f), new Vector3(2.0f, 2.0f, 2.0f)),
            });
        }

        return items;
    }

    private static void SwapSeveral(IRenderTree tree)
    {
        for (int i = 0; i < 64; i++)
            tree.Swap();
    }

    private sealed class TestRenderItem : IOctreeItem
    {
        public bool ShouldRender => true;
        public IRenderableBase? Owner => null;
        public AABB? LocalCullingVolume { get; init; }
        public Matrix4x4 CullingOffsetMatrix { get; set; } = Matrix4x4.Identity;
        public OctreeNodeBase? OctreeNode { get; set; }
    }
}
