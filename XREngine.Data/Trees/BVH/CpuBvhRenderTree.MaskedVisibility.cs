using XREngine.Data.Geometry;

namespace XREngine.Data.Trees;

public sealed partial class CpuBvhRenderTree<T>
{
    /// <summary>
    /// Collects exact visibility for up to eight logical views in one root-down
    /// traversal. Each node bound is loaded once; surviving view and per-view
    /// plane masks are propagated to both children without managed allocation.
    /// </summary>
    public void CollectVisibleMasked<TVisitor>(
        ReadOnlySpan<CpuBvhFrustum> views,
        ref TVisitor visitor)
        where TVisitor : struct, ICpuBvhMaskedVisitor<T>
    {
        if (views.Length is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(views), "Masked traversal supports one to eight logical views.");

        ValidateMaskedViews(views);
        EnsurePublished();
        Snapshot snapshot = AcquireSnapshot();
        try
        {
            ulong activeViewMask = (1UL << views.Length) - 1UL;
            ulong activePlaneMasks = 0UL;
            for (int i = 0; i < views.Length; i++)
                activePlaneMasks |= 0x3FUL << (i * 6);

            for (int i = 0; i < snapshot.UnboundedCount; i++)
            {
                T item = snapshot.UnboundedItems[i];
                if (item.ShouldRender)
                    visitor.Visit(item, activeViewMask);
            }

            Span<MaskedTraversalEntry> stack = stackalloc MaskedTraversalEntry[TraversalStackCapacity];
            int stackCount = 0;
            if (snapshot.RootIndex >= 0)
                stack[stackCount++] = new(snapshot.RootIndex, activeViewMask, activePlaneMasks);

            long internals = 0;
            long leaves = 0;
            long primitives = snapshot.UnboundedCount;
            long planeTests = 0;
            long maskEliminations = 0;
            while (stackCount > 0)
            {
                MaskedTraversalEntry traversal = stack[--stackCount];
                ref readonly FlatNode node = ref snapshot.Nodes[traversal.NodeIndex];
                ClassifyMaskedBounds(
                    views,
                    node.Bounds,
                    traversal.ViewMask,
                    traversal.PlaneMasks,
                    out ulong childViewMask,
                    out ulong childPlaneMasks,
                    ref planeTests,
                    ref maskEliminations);
                if (childViewMask == 0UL)
                    continue;

                if (node.IsLeaf)
                {
                    leaves++;
                    int end = node.First + node.Count;
                    for (int i = node.First; i < end; i++)
                    {
                        ref readonly Entry entry = ref snapshot.Entries[i];
                        T item = entry.Record.Item;
                        if (!item.ShouldRender)
                            continue;

                        primitives++;
                        ClassifyMaskedBounds(
                            views,
                            entry.Bounds,
                            childViewMask,
                            childPlaneMasks,
                            out ulong itemViewMask,
                            out _,
                            ref planeTests,
                            ref maskEliminations);
                        if (itemViewMask != 0UL)
                            visitor.Visit(item, itemViewMask);
                    }

                    continue;
                }

                internals++;
                if (stackCount + 2 > stack.Length)
                {
                    Interlocked.Increment(ref _traversalStackOverflowCount);
                    CollectVisibleMaskedRecursive(snapshot, node.Left, views, childViewMask, childPlaneMasks, ref visitor);
                    CollectVisibleMaskedRecursive(snapshot, node.Right, views, childViewMask, childPlaneMasks, ref visitor);
                    continue;
                }

                stack[stackCount++] = new(node.Right, childViewMask, childPlaneMasks);
                stack[stackCount++] = new(node.Left, childViewMask, childPlaneMasks);
            }

            Interlocked.Add(ref _visitedInternalNodeCount, internals);
            Interlocked.Add(ref _visitedLeafCount, leaves);
            Interlocked.Add(ref _visitedPrimitiveCount, primitives);
            Interlocked.Add(ref _frustumPlaneTestCount, planeTests);
            Interlocked.Add(ref _frustumPlaneMaskEliminationCount, maskEliminations);
        }
        finally
        {
            ReleaseSnapshot(snapshot);
        }
    }

    private static void ValidateMaskedViews(ReadOnlySpan<CpuBvhFrustum> views)
    {
        for (int i = 0; i < views.Length; i++)
        {
            int parent = views[i].ParentViewIndex;
            if (parent < -1 || parent >= i)
                throw new ArgumentException("A masked view parent must precede its child.", nameof(views));
            if (views[i].ParentContainsView && parent < 0)
                throw new ArgumentException("Validated containment requires a parent view.", nameof(views));
        }
    }

    private static void ClassifyMaskedBounds(
        ReadOnlySpan<CpuBvhFrustum> views,
        AABB bounds,
        ulong inputViewMask,
        ulong inputPlaneMasks,
        out ulong outputViewMask,
        out ulong outputPlaneMasks,
        ref long planeTests,
        ref long maskEliminations)
    {
        outputViewMask = inputViewMask;
        outputPlaneMasks = inputPlaneMasks;
        for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
        {
            ulong viewBit = 1UL << viewIndex;
            if ((outputViewMask & viewBit) == 0UL)
                continue;

            ref readonly CpuBvhFrustum view = ref views[viewIndex];
            if (view.ParentContainsView && (outputViewMask & (1UL << view.ParentViewIndex)) == 0UL)
            {
                outputViewMask &= ~viewBit;
                continue;
            }

            int planeShift = viewIndex * 6;
            byte planeMask = (byte)((inputPlaneMasks >> planeShift) & 0x3FUL);
            EContainment containment = ClassifyFrustumNode(
                view.Frustum,
                bounds,
                planeMask,
                out byte childPlaneMask,
                ref planeTests,
                ref maskEliminations);
            if (containment == EContainment.Disjoint)
            {
                outputViewMask &= ~viewBit;
                outputPlaneMasks &= ~(0x3FUL << planeShift);
                continue;
            }

            outputPlaneMasks = (outputPlaneMasks & ~(0x3FUL << planeShift))
                | ((ulong)childPlaneMask << planeShift);
        }
    }

    private static void CollectVisibleMaskedRecursive<TVisitor>(
        Snapshot snapshot,
        int nodeIndex,
        ReadOnlySpan<CpuBvhFrustum> views,
        ulong viewMask,
        ulong planeMasks,
        ref TVisitor visitor)
        where TVisitor : struct, ICpuBvhMaskedVisitor<T>
    {
        ref readonly FlatNode node = ref snapshot.Nodes[nodeIndex];
        long planeTests = 0;
        long maskEliminations = 0;
        ClassifyMaskedBounds(
            views,
            node.Bounds,
            viewMask,
            planeMasks,
            out ulong childViewMask,
            out ulong childPlaneMasks,
            ref planeTests,
            ref maskEliminations);
        if (childViewMask == 0UL)
            return;

        if (node.IsLeaf)
        {
            int end = node.First + node.Count;
            for (int i = node.First; i < end; i++)
            {
                ref readonly Entry entry = ref snapshot.Entries[i];
                T item = entry.Record.Item;
                if (!item.ShouldRender)
                    continue;

                ClassifyMaskedBounds(
                    views,
                    entry.Bounds,
                    childViewMask,
                    childPlaneMasks,
                    out ulong itemViewMask,
                    out _,
                    ref planeTests,
                    ref maskEliminations);
                if (itemViewMask != 0UL)
                    visitor.Visit(item, itemViewMask);
            }
            return;
        }

        CollectVisibleMaskedRecursive(snapshot, node.Left, views, childViewMask, childPlaneMasks, ref visitor);
        CollectVisibleMaskedRecursive(snapshot, node.Right, views, childViewMask, childPlaneMasks, ref visitor);
    }

    private readonly record struct MaskedTraversalEntry(
        int NodeIndex,
        ulong ViewMask,
        ulong PlaneMasks);
}
