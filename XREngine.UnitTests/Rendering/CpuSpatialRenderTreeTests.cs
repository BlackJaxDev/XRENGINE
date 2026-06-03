using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering.Info;

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

    [Test]
    public void CpuBvhFrustumCullMatchesOctreeForEdgeIntersectingItems()
    {
        var bounds = AABB.FromCenterSize(new Vector3(0.0f, 0.0f, -40.0f), new Vector3(180.0f, 120.0f, 120.0f));
        var octree = new Octree<TestRenderItem>(bounds);
        var bvh = new CpuBvhRenderTree<TestRenderItem>(bounds);
        List<TestRenderItem> items = CreateFrustumParityItems();
        var frustum = new Frustum(
            55.0f,
            16.0f / 9.0f,
            0.5f,
            80.0f,
            new Vector3(0.0f, 0.0f, -1.0f),
            Vector3.UnitY,
            Vector3.Zero);

        octree.AddRange(items);
        bvh.AddRange(items);
        SwapSeveral(octree);
        SwapSeveral(bvh);

        int[] octreeVisible = CollectVisibleIds(octree, frustum);
        int[] bvhVisible = CollectVisibleIds(bvh, frustum);

        bvhVisible.ShouldBe(octreeVisible);
    }

    [Test]
    public void CpuBvhFrustumCullMatchesOctreeWhenLookingStraightDown()
    {
        var bounds = AABB.FromCenterSize(new Vector3(0.0f, 40.0f, 0.0f), new Vector3(180.0f, 120.0f, 180.0f));
        var octree = new Octree<TestRenderItem>(bounds);
        var bvh = new CpuBvhRenderTree<TestRenderItem>(bounds);
        List<TestRenderItem> items = CreateTopDownFrustumParityItems();
        var frustum = new Frustum(
                55.0f,
                16.0f / 9.0f,
                0.5f,
                80.0f,
                new Vector3(0.0f, 0.0f, -1.0f),
                Vector3.UnitY,
                Vector3.Zero)
            .TransformedBy(Matrix4x4.CreateRotationX(-MathF.PI * 0.5f) * Matrix4x4.CreateTranslation(0.0f, 80.0f, 0.0f));

        octree.AddRange(items);
        bvh.AddRange(items);
        SwapSeveral(octree);
        SwapSeveral(bvh);

        int[] octreeVisible = CollectVisibleIds(octree, frustum);
        int[] bvhVisible = CollectVisibleIds(bvh, frustum);

        bvhVisible.ShouldBe(octreeVisible);
    }

    [Test]
    public void CpuBvhMoveRebuildsVisibility()
    {
        var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var item = new TestRenderItem
        {
            LocalCullingVolume = AABB.FromCenterSize(new Vector3(-20.0f, 0.0f, 0.0f), new Vector3(2.0f)),
        };

        tree.Add(item);
        tree.Swap();

        CountVisible(tree, AABB.FromCenterSize(new Vector3(-20.0f, 0.0f, 0.0f), new Vector3(4.0f))).ShouldBe(1);
        CountVisible(tree, AABB.FromCenterSize(new Vector3(20.0f, 0.0f, 0.0f), new Vector3(4.0f))).ShouldBe(0);

        item.LocalCullingVolume = AABB.FromCenterSize(new Vector3(20.0f, 0.0f, 0.0f), new Vector3(2.0f));
        item.OctreeNode.ShouldNotBeNull();
        item.OctreeNode!.QueueItemMoved(item);
        tree.Swap();

        CountVisible(tree, AABB.FromCenterSize(new Vector3(-20.0f, 0.0f, 0.0f), new Vector3(4.0f))).ShouldBe(0);
        CountVisible(tree, AABB.FromCenterSize(new Vector3(20.0f, 0.0f, 0.0f), new Vector3(4.0f))).ShouldBe(1);
    }

    [Test]
    public void RenderInfo3D_NullCullingVolumeMovesCpuBvhItemToUnboundedLane()
    {
        var tree = new CpuBvhRenderTree<RenderInfo3D>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var item = RenderInfo3D.New(new TestRenderable());
        item.LocalCullingVolume = AABB.FromCenterSize(new Vector3(-20.0f, -20.0f, -20.0f), new Vector3(2.0f));

        tree.Add(item);
        tree.Swap();

        CountVisible(tree, AABB.FromCenterSize(new Vector3(20.0f, 20.0f, 20.0f), new Vector3(4.0f))).ShouldBe(0);

        item.LocalCullingVolume = null;
        tree.Swap();

        tree.GetOccupancyStats().UnboundedItemCount.ShouldBe(1);
        CountVisible(tree, AABB.FromCenterSize(new Vector3(20.0f, 20.0f, 20.0f), new Vector3(4.0f))).ShouldBe(1);
    }

    [Test]
    public void RenderInfo3D_NullCullingVolumeMovesOctreeItemToAlwaysVisibleRoot()
    {
        var tree = new Octree<RenderInfo3D>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var item = RenderInfo3D.New(new TestRenderable());
        item.LocalCullingVolume = AABB.FromCenterSize(new Vector3(-20.0f, -20.0f, -20.0f), new Vector3(2.0f));

        tree.Add(item);
        tree.Swap();

        CountVisible(tree, AABB.FromCenterSize(new Vector3(20.0f, 20.0f, 20.0f), new Vector3(4.0f))).ShouldBe(0);

        item.LocalCullingVolume = null;
        tree.Swap();

        tree.GetOccupancyStats().UnboundedItemCount.ShouldBe(1);
        CountVisible(tree, AABB.FromCenterSize(new Vector3(20.0f, 20.0f, 20.0f), new Vector3(4.0f))).ShouldBe(1);
    }

    [Test]
    public void CpuBvhRemoveExcludesItem()
    {
        var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var items = CreateOriginCrossingItems(3);

        tree.AddRange(items);
        tree.Swap();
        tree.Remove(items[1]);
        tree.Swap();

        List<TestRenderItem> collected = [];
        tree.CollectAll(collected.Add);

        collected.Count.ShouldBe(2);
        collected.ShouldNotContain(items[1]);
        items[1].OctreeNode.ShouldBeNull();
    }

    [Test]
    public void CpuBvhCoalescesExistingAddThenRemoveAsRemove()
    {
        var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var item = new TestRenderItem
        {
            LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(2.0f)),
        };

        tree.Add(item);
        tree.Swap();

        tree.Add(item);
        tree.Remove(item);
        tree.Swap();

        List<TestRenderItem> collected = [];
        tree.CollectAll(collected.Add);

        collected.Count.ShouldBe(0);
        item.OctreeNode.ShouldBeNull();
    }

    [Test]
    [NonParallelizable]
    public void CpuBvhCoalescesTransientAddRemoveBeforeSwap()
    {
        var previousSwapHook = IRenderTree.OctreeSwapTimingHook;

        try
        {
            OctreeSwapTimingStats? swapStats = null;
            IRenderTree.OctreeSwapTimingHook = stats => swapStats = stats;

            var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
            var item = new TestRenderItem
            {
                LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(2.0f)),
            };

            tree.Add(item);
            tree.Remove(item);
            tree.Swap();

            swapStats.ShouldNotBeNull();
            swapStats.Value.DrainedCommandCount.ShouldBe(2);
            swapStats.Value.ExecutedCommandCount.ShouldBe(0);
            tree.GetOccupancyStats().ItemCount.ShouldBe(0);
            item.OctreeNode.ShouldBeNull();
        }
        finally
        {
            IRenderTree.OctreeSwapTimingHook = previousSwapHook;
        }
    }

    [Test]
    public void CpuBvhRaycastSyncAndAsyncHitBoundedItems()
    {
        var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
        var near = new TestRenderItem
        {
            LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(2.0f)),
        };
        var far = new TestRenderItem
        {
            LocalCullingVolume = AABB.FromCenterSize(new Vector3(20.0f, 0.0f, 0.0f), new Vector3(2.0f)),
        };
        var segment = new Segment(new Vector3(-5.0f, 0.0f, 0.0f), new Vector3(5.0f, 0.0f, 0.0f));

        tree.AddRange([near, far]);
        tree.Swap();

        var syncResults = new SortedDictionary<float, List<(TestRenderItem item, object? data)>>();
        tree.Raycast(segment, syncResults, DirectHit);

        syncResults.Count.ShouldBe(1);
        syncResults[0.0f].Single().item.ShouldBeSameAs(near);

        bool callbackCalled = false;
        var asyncResults = new SortedDictionary<float, List<(TestRenderItem item, object? data)>>();
        tree.RaycastAsync(
            segment,
            asyncResults,
            DirectHit,
            results =>
            {
                callbackCalled = true;
                results.Count.ShouldBe(1);
                results[0.0f].Single().item.ShouldBeSameAs(near);
            });

        tree.Swap();

        callbackCalled.ShouldBeTrue();
    }

    [Test]
    [NonParallelizable]
    public void CpuBvhReportsSwapAndRaycastTiming()
    {
        var previousSwapHook = IRenderTree.OctreeSwapTimingHook;
        var previousRaycastHook = IRenderTree.OctreeRaycastTimingHook;

        try
        {
            OctreeSwapTimingStats? swapStats = null;
            OctreeRaycastTimingStats? raycastStats = null;
            IRenderTree.OctreeSwapTimingHook = stats => swapStats = stats;
            IRenderTree.OctreeRaycastTimingHook = stats => raycastStats = stats;

            var tree = new CpuBvhRenderTree<TestRenderItem>(AABB.FromCenterSize(Vector3.Zero, new Vector3(100.0f)));
            var item = new TestRenderItem
            {
                LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(2.0f)),
            };
            var segment = new Segment(new Vector3(-5.0f, 0.0f, 0.0f), new Vector3(5.0f, 0.0f, 0.0f));

            tree.Add(item);
            tree.Swap();

            swapStats.ShouldNotBeNull();
            swapStats.Value.DrainedCommandCount.ShouldBe(1);
            swapStats.Value.ExecutedCommandCount.ShouldBe(1);
            swapStats.Value.MaxCommandKind.ShouldBe(EOctreeCommandKind.Add);

            tree.RaycastAsync(
                segment,
                new SortedDictionary<float, List<(TestRenderItem item, object? data)>>(),
                DirectHit,
                _ => { });

            tree.Swap();

            raycastStats.ShouldNotBeNull();
            raycastStats.Value.ProcessedCommandCount.ShouldBe(1);
        }
        finally
        {
            IRenderTree.OctreeSwapTimingHook = previousSwapHook;
            IRenderTree.OctreeRaycastTimingHook = previousRaycastHook;
        }
    }

    private static List<TestRenderItem> CreateOriginCrossingItems(int count)
    {
        var items = new List<TestRenderItem>(count);
        for (int i = 0; i < count; i++)
        {
            float x = -16.0f + i;
            items.Add(new TestRenderItem
            {
                Id = i,
                LocalCullingVolume = AABB.FromCenterSize(new Vector3(x, 0.0f, 0.0f), new Vector3(2.0f, 2.0f, 2.0f)),
            });
        }

        return items;
    }

    private static List<TestRenderItem> CreateFrustumParityItems()
    {
        const float fovY = 55.0f;
        const float aspect = 16.0f / 9.0f;
        float tanY = MathF.Tan(fovY * MathF.PI / 360.0f);
        var items = new List<TestRenderItem>(192);
        int id = 0;

        for (float depth = 4.0f; depth <= 76.0f; depth += 8.0f)
        {
            float halfY = tanY * depth;
            float halfX = halfY * aspect;
            float z = -depth;

            items.Add(CreateItem(id++, new Vector3(-halfX - 1.0f, 0.0f, z), new Vector3(4.0f)));
            items.Add(CreateItem(id++, new Vector3(halfX + 1.0f, 0.0f, z), new Vector3(4.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, -halfY - 1.0f, z), new Vector3(4.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, halfY + 1.0f, z), new Vector3(4.0f)));
            items.Add(CreateItem(id++, new Vector3(-halfX - 6.0f, 0.0f, z), new Vector3(2.0f)));
            items.Add(CreateItem(id++, new Vector3(halfX + 6.0f, 0.0f, z), new Vector3(2.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, -halfY - 6.0f, z), new Vector3(2.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, halfY + 6.0f, z), new Vector3(2.0f)));

            for (int y = -1; y <= 1; y++)
            {
                for (int x = -2; x <= 2; x++)
                {
                    Vector3 center = new(halfX * x * 0.35f, halfY * y * 0.45f, z);
                    items.Add(CreateItem(id++, center, new Vector3(1.5f)));
                }
            }
        }

        return items;
    }

    private static List<TestRenderItem> CreateTopDownFrustumParityItems()
    {
        const float fovY = 55.0f;
        const float aspect = 16.0f / 9.0f;
        const float cameraY = 80.0f;
        float tanY = MathF.Tan(fovY * MathF.PI / 360.0f);
        var items = new List<TestRenderItem>(192);
        int id = 0;

        for (float depth = 4.0f; depth <= 76.0f; depth += 8.0f)
        {
            float halfZ = tanY * depth;
            float halfX = halfZ * aspect;
            float y = cameraY - depth;

            items.Add(CreateItem(id++, new Vector3(-halfX - 1.0f, y, 0.0f), new Vector3(4.0f, 10.0f, 2.0f)));
            items.Add(CreateItem(id++, new Vector3(halfX + 1.0f, y, 0.0f), new Vector3(4.0f, 10.0f, 2.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, y, -halfZ - 1.0f), new Vector3(2.0f, 10.0f, 4.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, y, halfZ + 1.0f), new Vector3(2.0f, 10.0f, 4.0f)));
            items.Add(CreateItem(id++, new Vector3(-halfX - 6.0f, y, 0.0f), new Vector3(2.0f)));
            items.Add(CreateItem(id++, new Vector3(halfX + 6.0f, y, 0.0f), new Vector3(2.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, y, -halfZ - 6.0f), new Vector3(2.0f)));
            items.Add(CreateItem(id++, new Vector3(0.0f, y, halfZ + 6.0f), new Vector3(2.0f)));

            for (int z = -1; z <= 1; z++)
            {
                for (int x = -2; x <= 2; x++)
                {
                    Vector3 center = new(halfX * x * 0.35f, y, halfZ * z * 0.45f);
                    items.Add(CreateItem(id++, center, new Vector3(1.5f, 8.0f, 1.5f)));
                }
            }
        }

        return items;
    }

    private static TestRenderItem CreateItem(int id, Vector3 center, Vector3 size)
        => new()
        {
            Id = id,
            LocalCullingVolume = AABB.FromCenterSize(center, size),
        };

    private static void SwapSeveral(IRenderTree tree)
    {
        for (int i = 0; i < 64; i++)
            tree.Swap();
    }

    private static int CountVisible(CpuBvhRenderTree<TestRenderItem> tree, AABB volume)
    {
        int count = 0;
        tree.CollectVisible(volume, false, _ => count++, Intersects);
        return count;
    }

    private static int CountVisible(I3DRenderTree<RenderInfo3D> tree, AABB volume)
    {
        int count = 0;
        tree.CollectVisible(volume, false, _ => count++, static (item, cullingVolume, containsOnly) => item.Intersects(cullingVolume, containsOnly));
        return count;
    }

    private static int[] CollectVisibleIds(I3DRenderTree<TestRenderItem> tree, IVolume volume)
    {
        List<int> ids = [];
        tree.CollectVisible(volume, false, item => ids.Add(item.Id), IntersectsBox);
        ids.Sort();
        return [.. ids];
    }

    private static bool Intersects(TestRenderItem item, IVolume? cullingVolume, bool containsOnly)
    {
        if (cullingVolume is null)
            return true;

        Box? worldCullingVolume = item.LocalCullingVolume?.ToBox(item.CullingOffsetMatrix);
        if (worldCullingVolume is null)
            return true;

        AABB itemBounds = worldCullingVolume.Value.GetAABB(true);
        EContainment containment = cullingVolume.ContainsAABB(itemBounds);
        if (containment == EContainment.Disjoint &&
            cullingVolume is AABB aabb &&
            aabb.Intersects(itemBounds))
        {
            containment = EContainment.Intersects;
        }

        return containsOnly
            ? containment == EContainment.Contains
            : containment != EContainment.Disjoint;
    }

    private static bool IntersectsBox(TestRenderItem item, IVolume? cullingVolume, bool containsOnly)
    {
        if (cullingVolume is null)
            return true;

        Box? worldCullingVolume = ((IOctreeItem)item).WorldCullingVolume;
        if (worldCullingVolume is null)
            return true;

        EContainment containment = cullingVolume.ContainsBox(worldCullingVolume.Value);
        return containsOnly
            ? containment == EContainment.Contains
            : containment != EContainment.Disjoint;
    }

    private static (float? distance, object? data) DirectHit(TestRenderItem item, Segment segment)
        => (0.0f, item);

    private sealed class TestRenderItem : IOctreeItem
    {
        public int Id { get; init; }
        public bool ShouldRender => true;
        public IRenderableBase? Owner => null;
        public AABB? LocalCullingVolume { get; set; }
        public Matrix4x4 CullingOffsetMatrix { get; set; } = Matrix4x4.Identity;
        public OctreeNodeBase? OctreeNode { get; set; }
    }

    private sealed class TestRenderable : IRenderable
    {
        public RenderInfo[] RenderedObjects => [];
        public float TransformDepth => 0.0f;
    }
}
