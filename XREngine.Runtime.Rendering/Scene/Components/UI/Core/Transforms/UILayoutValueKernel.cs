using System.Numerics;
using System.Threading;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Pure numeric layout shared by worker and serial verification.</summary>
internal static class UILayoutValueKernel
{
    public static UILayoutNodeOutput[] Run(
        ReadOnlySpan<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children,
        BoundingRectangleF rootBounds)
    {
        return RunPasses(UILayoutValuePageView<UILayoutNodeInput>.CopyFrom(nodes), children, rootBounds, null, null, null,
            CancellationToken.None).ToArray();
    }

    internal static UILayoutNodeOutput[] Run(
        UILayoutValuePageView<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children, BoundingRectangleF rootBounds)
        => RunPasses(nodes, children, rootBounds, null, null, null,
            CancellationToken.None).ToArray();

    /// <summary>Reuse measured subtrees only when aggregate dependency keys and constraints match.</summary>
    internal static UILayoutNodeOutput[] Run(
        UILayoutSnapshot input, UILayoutSnapshot? previous, UILayoutNodeOutput[]? previousOutputs,
        CancellationToken cancellation = default)
        => RunPaged(input, previous, previousOutputs is null ? null :
            UILayoutOutputPageView.CopyFrom(previousOutputs), cancellation).ToArray();

    internal static UILayoutOutputPageView RunPaged(
        UILayoutSnapshot input, UILayoutSnapshot? previous,
        UILayoutOutputPageView? previousOutputs, CancellationToken cancellation = default)
    {
        foreach (int index in input.TextIndices)
        {
            if (input.Nodes[index].TextInput is not { ShapingRequest: not null } text)
                continue;
            UITextShapingBackendState backendState = UITextShapingBackendRegistry.State;
            if (backendState.Backend is not IUITextWorkerShapingBackend backend ||
                text.ShapingBackendGeneration != backendState.Generation)
                throw new NotSupportedException("UI.WorkerShapingBackendUnavailable: the captured backend changed.");
            input = input.ResolveShaping(backend, null, null, cancellation);
            break;
        }
        return RunPasses(input.Nodes, input.Children, input.RootBounds,
            input, previous, previousOutputs, cancellation);
    }

    internal static void ValidateOutput(int index, in UILayoutNodeOutput output)
    {
            if (!Finite(output.RawSize) || !Finite(output.DesiredSize) ||
                !Finite(output.ActualSize) || !Finite(output.LocalTranslation) ||
                !Finite(output.PlacementTranslation) || !Finite(output.AllocatedBounds.Translation) ||
                !Finite(output.AllocatedBounds.Extents) || output.ActualSize.X < 0 ||
                output.ActualSize.Y < 0)
                throw new InvalidOperationException($"Layout produced invalid geometry at node {index}.");
    }

    private static bool Finite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static UILayoutOutputPageView RunPasses(
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        BoundingRectangleF rootBounds,
        UILayoutSnapshot? current, UILayoutSnapshot? previous,
        UILayoutOutputPageView? previousOutputs, CancellationToken cancellation)
    {
        if (nodes.Length == 0)
            return new UILayoutOutputPageBuilder(0).Freeze();
        if (current is not null)
        {
            foreach (int i in current.TextIndices)
                if (nodes[i].TextInput?.ShapingRequest is not null)
                    throw new InvalidOperationException("UI.ShapingRequestUnresolved: shape text before measuring layout.");
        }
        else
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i].TextInput?.ShapingRequest is not null)
                    throw new InvalidOperationException("UI.ShapingRequestUnresolved: shape text before measuring layout.");
        UILayoutOutputPageView? arranged = null;
        for (int pass = 0; pass < 3; pass++)
        {
            cancellation.ThrowIfCancellationRequested();
            UILayoutOutputPageBuilder outputs = new(nodes.Length, pass == 0 ? previousOutputs : null);
            Measure(0, rootBounds.Extents, nodes, children, outputs,
                pass == 0 ? current : null, previous, previousOutputs, cancellation, arranged);
            Arrange(0, ResolveRootBounds(rootBounds, outputs[0]), Vector2.Zero, true,
                nodes, children, outputs,
                pass == 0 && current is not null
                    ? new UILayoutArrangeCache(current, previous, previousOutputs, cancellation)
                    : null);
            outputs.ValidateChangedPages();
            UILayoutOutputPageView result = outputs.Freeze();
            if (!NeedsTextRemeasure(nodes, result))
                return result;
            if (pass == 2)
                throw new InvalidOperationException("Text layout did not converge within three measure passes.");
            arranged = result;
        }
        throw new InvalidOperationException("Text layout pass limit was not reached correctly.");
    }

    private static bool NeedsTextRemeasure(UILayoutValuePageView<UILayoutNodeInput> nodes,
        UILayoutOutputPageView outputs)
    {
        for (int page = 0; page < outputs.ChangedPages.Length; page++)
        {
            if (!outputs.ChangedPages[page])
                continue;
            int end = Math.Min((page + 1) * UILayoutOutputPageView.PageSize, nodes.Length);
            for (int i = page * UILayoutOutputPageView.PageSize; i < end; i++)
            {
                ref readonly UILayoutNodeInput node = ref nodes[i];
                if (node.Collapsed || node.TextInput is null || outputs[i].TextResult is not { } text)
                    continue;
                if (node.Width is null && node.CapturedAutoWidth is null &&
                    MathF.Abs(outputs[i].RawSize.X - text.IntrinsicSize.X) > 0.01f)
                    return true;
                if (node.Height is null && node.CapturedAutoHeight is null &&
                    MathF.Abs(outputs[i].RawSize.Y - text.IntrinsicSize.Y) > 0.01f)
                    return true;
            }
        }
        return false;
    }

    private static BoundingRectangleF ResolveRootBounds(
        BoundingRectangleF constraint, in UILayoutNodeOutput output)
        => new(Vector2.Zero, new Vector2(
            float.IsPositiveInfinity(constraint.Width) ? output.RawSize.X : constraint.Width,
            float.IsPositiveInfinity(constraint.Height) ? output.RawSize.Y : constraint.Height));

    private static Vector2 Measure(
        int index,
        Vector2 constraint,
        UILayoutValuePageView<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs,
        UILayoutSnapshot? current,
        UILayoutSnapshot? previous,
        UILayoutOutputPageView? previousOutputs,
        CancellationToken cancellation,
        UILayoutOutputPageView? arranged)
    {
        cancellation.ThrowIfCancellationRequested();
        outputs.RecordVisit();
        ref readonly UILayoutNodeInput node = ref nodes[index];
        if (current is not null && previous is not null && previousOutputs is not null &&
            !current.IsDirtySince(previous, index) &&
            index < previous.Nodes.Length && index < previousOutputs.Length &&
            current.Identities[index] == previous.Identities[index] &&
            current.MeasureVersions[index] == previous.MeasureVersions[index] &&
            current.Revisions[index].Text == previous.Revisions[index].Text &&
            previous.Nodes[index].Operator == node.Operator &&
            Equals(previous.Nodes[index].TextInput, node.TextInput) &&
            previousOutputs[index].MeasureConstraint == constraint)
        {
            return outputs[index].DesiredSize;
        }
        ref UILayoutNodeOutput output = ref outputs.Edit(index);
        output = default;
        if (node.Collapsed)
        {
            output.MeasureConstraint = constraint;
            return Vector2.Zero;
        }

        Vector2 innerConstraint = new(
            MathF.Max(0, (node.Width ?? constraint.X) - node.Padding.X - node.Padding.Z),
            MathF.Max(0, (node.Height ?? constraint.Y) - node.Padding.Y - node.Padding.W));
        bool horizontalList = node.Operator == UILayoutOperator.List && (node.OperatorFlags & 1) != 0;
        bool verticalList = node.Operator == UILayoutOperator.List && !horizontalList;
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childIndex = children[node.FirstChild + i].NodeIndex;
            Vector2 childConstraint = innerConstraint;
            if (horizontalList)
                childConstraint.X = float.IsNaN(node.OperatorValues.X) ? float.PositiveInfinity : node.OperatorValues.X;
            else if (verticalList)
                childConstraint.Y = float.IsNaN(node.OperatorValues.X) ? float.PositiveInfinity : node.OperatorValues.X;
            if (arranged is not null)
                childConstraint = arranged[childIndex].ActualSize;
            if (current is not null && previous is not null && previousOutputs is not null &&
                !current.IsDirtySince(previous, childIndex) && childIndex < previousOutputs.Length &&
                previousOutputs[childIndex].MeasureConstraint == childConstraint)
                continue;
            Measure(childIndex, childConstraint, nodes, children, outputs,
                current, previous, previousOutputs, cancellation, arranged);
        }

        if (node.TextInput is { FontSize: null } &&
            !float.IsFinite(node.Width ?? constraint.X) &&
            !float.IsFinite(node.Height ?? constraint.Y))
            throw new InvalidOperationException("Auto-fit text needs a finite layout constraint.");
        Vector2? textSize = node.TextInput is null ? null :
            UITextLayoutKernel.Measure(node.TextInput,
                new Vector2(node.Width ?? constraint.X, node.Height ?? constraint.Y)).IntrinsicSize;
        float rawWidth = node.Width ?? node.CapturedAutoWidth ?? textSize?.X ?? MeasureRawAxis(index, true, nodes, children, outputs);
        float rawHeight = node.Height ?? node.CapturedAutoHeight ?? textSize?.Y ?? MeasureRawAxis(index, false, nodes, children, outputs);
        float measuredWidth = node.Width ?? node.CapturedAutoWidth ?? textSize?.X ?? MeasureAutoAxis(index, true, nodes, children, outputs);
        float measuredHeight = node.Height ?? node.CapturedAutoHeight ?? textSize?.Y ?? MeasureAutoAxis(index, false, nodes, children, outputs);
        output.MeasureConstraint = constraint;
        output.RawSize = new Vector2(rawWidth, rawHeight);
        Vector2 desired = new(Clamp(measuredWidth, node.MinWidth, node.MaxWidth), Clamp(measuredHeight, node.MinHeight, node.MaxHeight));
        if (node.Operator != UILayoutOperator.Transform && node.Operator != UILayoutOperator.Rotation)
            desired += new Vector2(node.Margins.X + node.Margins.Z, node.Margins.Y + node.Margins.W);
        output.DesiredSize = desired;
        return desired;
    }

    private static float MeasureAutoAxis(
        int index,
        bool horizontal,
        UILayoutValuePageView<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs)
    {
        ref readonly UILayoutNodeInput node = ref nodes[index];
        bool isListAxis = node.Operator == UILayoutOperator.List && ((node.OperatorFlags & 1) != 0) == horizontal;
        if (isListAxis && (node.OperatorFlags & 16) != 0)
            return node.OperatorValues.W;
        float size = 0;
        int count = 0;
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childIndex = children[node.FirstChild + i].NodeIndex;
            ref readonly UILayoutNodeInput child = ref nodes[childIndex];
            if (child.Collapsed || (horizontal ? child.ExcludeAutoWidth : child.ExcludeAutoHeight))
                continue;
            ref readonly UILayoutNodeOutput childOutput = ref outputs[childIndex];
            if (node.Operator is UILayoutOperator.Transform or UILayoutOperator.Rotation)
            {
                size = MathF.Max(size, horizontal ? childOutput.DesiredSize.X : childOutput.DesiredSize.Y);
                continue;
            }
            float childSize = horizontal ? childOutput.RawSize.X + child.Margins.X + child.Margins.Z
                : childOutput.RawSize.Y + child.Margins.Y + child.Margins.W;
            if (isListAxis)
            {
                float itemSize = node.OperatorValues.X;
                size += float.IsNaN(itemSize) ? (horizontal ? childOutput.DesiredSize.X : childOutput.DesiredSize.Y) : itemSize;
                count++;
            }
            else
                size = MathF.Max(size, childSize);
        }

        if (isListAxis && count > 1)
            size += (count - 1) * node.OperatorValues.Y;
        return size;
    }

    private static float MeasureRawAxis(
        int index, bool horizontal,
        UILayoutValuePageView<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs)
    {
        ref readonly UILayoutNodeInput node = ref nodes[index];
        if (node.Operator is UILayoutOperator.Transform or UILayoutOperator.Rotation)
            return MeasureAutoAxis(index, horizontal, nodes, children, outputs);
        bool listAxis = node.Operator == UILayoutOperator.List && ((node.OperatorFlags & 1) != 0) == horizontal;
        if (listAxis && (node.OperatorFlags & 16) != 0)
            return node.OperatorValues.W;
        float size = 0;
        int count = 0;
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childIndex = children[node.FirstChild + i].NodeIndex;
            ref readonly UILayoutNodeInput child = ref nodes[childIndex];
            if (child.Collapsed || (horizontal ? child.ExcludeAutoWidth : child.ExcludeAutoHeight))
                continue;
            float childSize = horizontal ? outputs[childIndex].RawSize.X + child.Margins.X + child.Margins.Z
                : outputs[childIndex].RawSize.Y + child.Margins.Y + child.Margins.W;
            if (listAxis)
            {
                float listRaw = horizontal ? outputs[childIndex].RawSize.X : outputs[childIndex].RawSize.Y;
                size += float.IsNaN(node.OperatorValues.X) ? listRaw : node.OperatorValues.X;
                count++;
            }
            else
                size = MathF.Max(size, childSize);
        }
        if (listAxis && count > 1)
            size += (count - 1) * node.OperatorValues.Y;
        return size;
    }

    internal static void Arrange(
        int index,
        BoundingRectangleF slot,
        Vector2 placement,
        bool inheritedVisible,
        UILayoutValuePageView<UILayoutNodeInput> nodes,
        ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs,
        UILayoutArrangeCache? cache = null)
    {
        if (cache is not null)
            cache.Cancellation.ThrowIfCancellationRequested();
        outputs.RecordVisit();
        if (cache?.Previous is { } previous && cache.PreviousOutputs is { } prior &&
            !cache.Current.IsDirtySince(previous, index) &&
            index < previous.Nodes.Length && index < prior.Length &&
            cache.Current.Identities[index] == previous.Identities[index] &&
            cache.Current.ArrangeVersions[index] == previous.ArrangeVersions[index] &&
            cache.Current.MeasureVersions[index] == previous.MeasureVersions[index] &&
            previous.Nodes[index].Operator == nodes[index].Operator &&
            Equals(previous.Nodes[index].TextInput, nodes[index].TextInput) &&
            EdgesSame(index, cache.Current, previous) &&
            prior[index].AllocatedBounds.Equals(slot) &&
            prior[index].PlacementTranslation == placement &&
            prior[index].Visible == (inheritedVisible && !nodes[index].Collapsed && !nodes[index].Hidden) &&
            prior[index].RawSize == outputs[index].RawSize &&
            prior[index].DesiredSize == outputs[index].DesiredSize)
        {
            return;
        }
        ref readonly UILayoutNodeInput node = ref nodes[index];
        ref UILayoutNodeOutput output = ref outputs.Edit(index);
        output.AllocatedBounds = slot;
        output.PlacementTranslation = placement;
        output.Visible = inheritedVisible && !node.Collapsed && !node.Hidden;

        if (node.Operator == UILayoutOperator.Transform || node.Operator == UILayoutOperator.Rotation)
        {
            output.ActualSize = slot.Extents;
            output.LocalTranslation = node.Translation;
        }
        else
        {
            ComputeBoundable(node, output.RawSize, slot.Extents, out output.LocalTranslation, out output.ActualSize);
            if (node.Operator == UILayoutOperator.Fitted)
                Fit(node, output.RawSize, slot.Extents, ref output.ActualSize);
        }
        if (node.TextInput is not null)
            output.TextResult = UITextLayoutKernel.Measure(node.TextInput, output.ActualSize);

        Vector2 padding = new(node.Padding.X, node.Padding.Y);
        BoundingRectangleF region = new(padding, output.ActualSize -
            new Vector2(node.Padding.X + node.Padding.Z, node.Padding.Y + node.Padding.W));
        switch (node.Operator)
        {
            case UILayoutOperator.List:
                ArrangeList(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            case UILayoutOperator.DualSplit:
                ArrangeDualSplit(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            case UILayoutOperator.Tab:
                ArrangeTabs(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            case UILayoutOperator.DockingRoot:
                ArrangeDockingRoot(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            case UILayoutOperator.Scrollable:
                ArrangeScrollable(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            case UILayoutOperator.Grid:
                UILayoutGridKernel.ArrangeGrid(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            case UILayoutOperator.MultiSplit:
                UILayoutMultiSplitKernel.ArrangeMultiSplit(node, region, nodes, children, outputs, output.Visible, cache);
                break;
            default:
                for (int i = 0; i < node.ChildCount; i++)
                {
                    int childIndex = children[node.FirstChild + i].NodeIndex;
                    if (cache?.Previous is { } oldSnapshot && cache.PreviousOutputs is { } priorOutputs &&
                        !cache.Current.IsDirtySince(oldSnapshot, childIndex) &&
                        index < priorOutputs.Length && childIndex < priorOutputs.Length &&
                        oldSnapshot.Nodes[index].Padding == node.Padding &&
                        priorOutputs[index].ActualSize == output.ActualSize &&
                        priorOutputs[index].Visible == output.Visible)
                        continue;
                    Arrange(childIndex, region, padding, output.Visible, nodes, children, outputs, cache);
                }
                break;
        }
    }

    private static bool EdgesSame(int index, UILayoutSnapshot current, UILayoutSnapshot previous)
    {
        UILayoutNodeInput node = current.Nodes[index];
        UILayoutNodeInput old = previous.Nodes[index];
        if (node.ChildCount != old.ChildCount)
            return false;
        for (int i = 0; i < node.ChildCount; i++)
            if (current.Children[node.FirstChild + i] != previous.Children[old.FirstChild + i])
                return false;
        return true;
    }

    private static void ComputeBoundable(
        in UILayoutNodeInput node, Vector2 rawSize, Vector2 parentSize,
        out Vector2 translation, out Vector2 size)
    {
        Vector2 min = node.MinAnchor * parentSize;
        Vector2 max = node.MaxAnchor * parentSize;
        bool pointX = MathF.Abs(max.X - min.X) < 0.0001f;
        bool pointY = MathF.Abs(max.Y - min.Y) < 0.0001f;
        size = new Vector2(
            pointX ? rawSize.X : max.X + (node.Width ?? 0) - min.X - node.Translation.X - node.Margins.X - node.Margins.Z,
            pointY ? rawSize.Y : max.Y + (node.Height ?? 0) - min.Y - node.Translation.Y - node.Margins.Y - node.Margins.W);
        size.X = Clamp(size.X, node.MinWidth, node.MaxWidth);
        size.Y = Clamp(size.Y, node.MinHeight, node.MaxHeight);
        translation = min - node.Pivot * size;
        if (pointX)
            translation.X += node.Translation.X + node.Margins.X * (1 - node.MinAnchor.X) - node.Margins.Z * node.MinAnchor.X;
        else
            translation.X += node.Margins.X;
        if (pointY)
            translation.Y += node.Translation.Y + node.Margins.Y * (1 - node.MinAnchor.Y) - node.Margins.W * node.MinAnchor.Y;
        else
            translation.Y += node.Margins.Y;
    }

    private static void Fit(in UILayoutNodeInput node, Vector2 rawSize, Vector2 parentSize, ref Vector2 size)
    {
        int mode = node.OperatorFlags & 3;
        if (mode == 1)
            size = parentSize;
        if (mode is not (2 or 3))
            return;
        float aspect = rawSize.Y > 0 ? rawSize.X / rawSize.Y : 0;
        if (!(aspect > 0) || !(parentSize.X > 0) || !(parentSize.Y > 0))
            return;
        float parentAspect = parentSize.X / parentSize.Y;
        bool widthControls = mode == 2 ? aspect > parentAspect : aspect <= parentAspect;
        size = widthControls ? new(parentSize.X, parentSize.X / aspect) : new(parentSize.Y * aspect, parentSize.Y);
    }

    private static void ArrangeTabs(
        in UILayoutNodeInput node, BoundingRectangleF region,
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs, bool visible, UILayoutArrangeCache? cache)
    {
        for (int i = 0; i < node.ChildCount; i++)
            Arrange(children[node.FirstChild + i].NodeIndex, region, region.Translation,
                visible && i == node.OperatorFlags, nodes, children, outputs, cache);
    }

    private static void ArrangeDockingRoot(
        in UILayoutNodeInput node, BoundingRectangleF region,
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs, bool visible, UILayoutArrangeCache? cache)
    {
        float left = node.OperatorValues.X;
        float right = node.OperatorValues.Y;
        float bottom = node.OperatorValues.Z;
        for (int i = 0; i < node.ChildCount; i++)
        {
            Vector2 position;
            Vector2 size;
            switch (i)
            {
                case 0:
                    position = new(left, bottom);
                    size = new(region.Width - left - right, region.Height - bottom);
                    break;
                case 1:
                    position = new(0, bottom);
                    size = new(left, region.Height - bottom);
                    break;
                case 2:
                    position = new(region.Width - right, bottom);
                    size = new(right, region.Height - bottom);
                    break;
                case 3:
                    position = Vector2.Zero;
                    size = new(region.Width, bottom);
                    break;
                default:
                    throw new InvalidOperationException("Docking root has more than four layout children.");
            }
            Arrange(children[node.FirstChild + i].NodeIndex,
                new BoundingRectangleF(position, size), position + region.Translation,
                visible, nodes, children, outputs, cache);
        }
    }

    private static void ArrangeScrollable(
        in UILayoutNodeInput node, BoundingRectangleF region,
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs, bool visible, UILayoutArrangeCache? cache)
    {
        Vector2 scroll = new(node.OperatorValues.X, node.OperatorValues.Y);
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childIndex = children[node.FirstChild + i].NodeIndex;
            bool providerList = nodes[childIndex].Operator == UILayoutOperator.List &&
                (nodes[childIndex].OperatorFlags & 16) != 0;
            Vector2 placement = providerList ? region.Translation : region.Translation - scroll;
            Arrange(childIndex, region, placement, visible, nodes, children, outputs, cache);
        }
    }

    private static void ArrangeList(
        in UILayoutNodeInput node, BoundingRectangleF region,
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs, bool visible, UILayoutArrangeCache? cache)
    {
        bool horizontal = (node.OperatorFlags & 1) != 0;
        float itemSize = node.OperatorValues.X;
        float spacing = node.OperatorValues.Y;
        float offset = node.OperatorValues.Z;
        bool provider = (node.OperatorFlags & 16) != 0;
        float extent = 0;
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childIndex = children[node.FirstChild + i].NodeIndex;
            if (!nodes[childIndex].Collapsed)
                extent += float.IsNaN(itemSize) ? (horizontal ? outputs[childIndex].DesiredSize.X : outputs[childIndex].DesiredSize.Y) : itemSize;
        }
        extent += MathF.Max(0, node.ChildCount - 1) * spacing;
        int alignment = (node.OperatorFlags >> 1) & 3;
        float cursor = horizontal ? -offset : region.Height + offset;
        if (alignment == 1)
            cursor += horizontal ? (region.Width - extent) * 0.5f : -(region.Height - extent) * 0.5f;
        else if (alignment == 2)
            cursor += horizontal ? region.Width - extent : -(region.Height - extent);
        for (int i = 0; i < node.ChildCount; i++)
        {
            int childIndex = children[node.FirstChild + i].NodeIndex;
            if (provider)
            {
                float stride = itemSize + spacing;
                float logical = children[node.FirstChild + i].Row * stride - offset;
                Vector2 pos = horizontal ? new(logical, 0) : new(0, region.Height - logical - itemSize);
                Vector2 extentSize = horizontal ? new(itemSize, region.Height) : new(region.Width, itemSize);
                Arrange(childIndex, new BoundingRectangleF(pos, extentSize), pos + region.Translation,
                    visible, nodes, children, outputs, cache);
                continue;
            }
            float axisSize = float.IsNaN(itemSize) ? (horizontal ? outputs[childIndex].DesiredSize.X : outputs[childIndex].DesiredSize.Y) : itemSize;
            if (!horizontal)
                cursor -= axisSize;
            Vector2 position = horizontal ? new(cursor, 0) : new(0, cursor);
            Vector2 size = horizontal ? new(axisSize, region.Height) : new(region.Width, axisSize);
            Arrange(childIndex, new BoundingRectangleF(position, size), position + region.Translation, visible, nodes, children, outputs, cache);
            cursor += horizontal ? axisSize + spacing : -spacing;
        }
    }

    private static void ArrangeDualSplit(
        in UILayoutNodeInput node, BoundingRectangleF region,
        UILayoutValuePageView<UILayoutNodeInput> nodes, ReadOnlySpan<UILayoutChildInput> children,
        UILayoutOutputPageBuilder outputs, bool visible, UILayoutArrangeCache? cache)
    {
        if (node.ChildCount == 0)
            return;
        int first = children[node.FirstChild].NodeIndex;
        if (node.ChildCount == 1)
        {
            Arrange(first, region, region.Translation, visible, nodes, children, outputs, cache);
            return;
        }

        int second = children[node.FirstChild + 1].NodeIndex;
        bool vertical = (node.OperatorFlags & 1) != 0;
        int fixedSide = (node.OperatorFlags >> 1) & 3;
        float extent = vertical ? region.Height : region.Width;
        float fixedSize = node.OperatorValues.Y;
        if (float.IsNaN(fixedSize))
        {
            int fixedChild = fixedSide == 1 ? first : second;
            fixedSize = vertical ? outputs[fixedChild].RawSize.Y : outputs[fixedChild].RawSize.X;
        }
        float firstSize = fixedSide == 0 ? extent * node.OperatorValues.X
            : fixedSide == 1 ? fixedSize : extent - fixedSize;
        float secondSize = extent - firstSize;
        float gap = node.OperatorValues.Z;
        Vector2 firstPos = vertical ? new(0, secondSize + gap) : Vector2.Zero;
        Vector2 secondPos = vertical ? Vector2.Zero : new(firstSize + gap, 0);
        Vector2 firstExtent = vertical ? new(region.Width, firstSize) : new(firstSize, region.Height);
        Vector2 secondExtent = vertical ? new(region.Width, secondSize) : new(secondSize, region.Height);
        Arrange(first, new BoundingRectangleF(firstPos, firstExtent), firstPos + region.Translation, visible, nodes, children, outputs, cache);
        Arrange(second, new BoundingRectangleF(secondPos, secondExtent), secondPos + region.Translation, visible, nodes, children, outputs, cache);
    }

    private static float Clamp(float value, float? min, float? max)
    {
        if (min.HasValue)
            value = MathF.Max(value, min.Value);
        if (max.HasValue)
            value = MathF.Min(value, max.Value);
        return value;
    }
}
