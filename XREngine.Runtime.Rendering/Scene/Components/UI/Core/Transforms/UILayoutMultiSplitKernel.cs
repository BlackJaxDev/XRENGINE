using System;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Divides a frozen split container into two or three bounded regions.</summary>
internal static class UILayoutMultiSplitKernel
{
    internal static void ArrangeMultiSplit(
        in UILayoutNodeInput node, BoundingRectangleF region,
        ReadOnlySpan<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutNodeOutput[] outputs, bool visible, UILayoutArrangeCache? cache = null)
    {
        if (node.ChildCount == 0)
            return;
        if (node.ChildCount > 3)
            throw new InvalidOperationException("A multi-split supports at most three children.");

        int arrangement = node.OperatorFlags & 3;
        bool vertical = arrangement is (int)UISplitArrangement.TopMiddleBottom or (int)UISplitArrangement.TopBottom;
        bool three = arrangement is (int)UISplitArrangement.LeftMiddleRight or (int)UISplitArrangement.TopMiddleBottom;
        float extent = vertical ? region.Height : region.Width;
        float gap = node.OperatorExtra;
        float firstPercent = node.OperatorValues.X;
        float secondPercent = node.OperatorValues.Y;
        float fixedFirst = node.OperatorValues.Z;
        float fixedLast = node.OperatorValues.W;
        if (!float.IsFinite(extent) || extent < 0 || !float.IsFinite(gap) || gap < 0 ||
            !float.IsFinite(firstPercent) || !float.IsFinite(secondPercent) ||
            firstPercent < 0 || firstPercent > 1 || secondPercent < 0 || secondPercent > 1 ||
            (!float.IsNaN(fixedFirst) && (!float.IsFinite(fixedFirst) || fixedFirst < 0)) ||
            (!float.IsNaN(fixedLast) && (!float.IsFinite(fixedLast) || fixedLast < 0)))
            throw new InvalidOperationException("Multi-split sizing values must be finite and nonnegative.");

        if (node.ChildCount == 1)
        {
            ArrangeChild(node, 0, region, region.Translation, nodes, children, outputs, visible, cache);
            return;
        }

        int count = three && node.ChildCount >= 3 ? 3 : 2;
        if (node.ChildCount > count || (count == 3 && secondPercent < firstPercent))
            throw new InvalidOperationException("Multi-split child count or split order is invalid.");
        gap = MathF.Min(gap, extent / (count - 1));
        float usable = MathF.Max(0, extent - gap * (count - 1));
        float first = float.IsNaN(fixedFirst) ? usable * firstPercent : fixedFirst;
        float last = float.IsNaN(fixedLast) ? usable * (three ? 1 - secondPercent : 1 - firstPercent) : fixedLast;
        first = Math.Clamp(first, 0, usable);
        last = Math.Clamp(last, 0, usable - first);
        float middle = count == 3 ? usable - first - last : 0;
        if (count == 2)
        {
            if (float.IsNaN(fixedFirst))
                first = usable - last;
            else if (float.IsNaN(fixedLast))
                last = usable - first;
        }

        float firstOffset = vertical ? extent - first : 0;
        float middleOffset = vertical ? last + gap : first + gap;
        float lastOffset = vertical ? 0 : first + (count == 3 ? middle + gap * 2 : gap);
        ArrangeSlot(node, 0, firstOffset, first, region, vertical, nodes, children, outputs, visible, cache);
        if (count == 3)
            ArrangeSlot(node, 1, middleOffset, middle, region, vertical, nodes, children, outputs, visible, cache);
        ArrangeSlot(node, count - 1, lastOffset, last, region, vertical, nodes, children, outputs, visible, cache);
    }

    private static void ArrangeSlot(
        in UILayoutNodeInput node, int ordinal, float offset, float length,
        BoundingRectangleF region, bool vertical, ReadOnlySpan<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children, UILayoutNodeOutput[] outputs, bool visible, UILayoutArrangeCache? cache = null)
    {
        Vector2 position = vertical ? new(0, offset) : new(offset, 0);
        Vector2 size = vertical ? new(region.Width, length) : new(length, region.Height);
        ArrangeChild(node, ordinal, new BoundingRectangleF(position, size),
            position + region.Translation, nodes, children, outputs, visible, cache);
    }

    private static void ArrangeChild(
        in UILayoutNodeInput node, int ordinal, BoundingRectangleF bounds,
        Vector2 placement, ReadOnlySpan<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children, UILayoutNodeOutput[] outputs, bool visible, UILayoutArrangeCache? cache = null)
    {
        UILayoutValueKernel.Arrange(children[node.FirstChild + ordinal].NodeIndex,
            bounds, placement, visible, nodes, children, outputs, cache);
    }
}
