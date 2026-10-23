using System.Numerics;
using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI.State;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

/// <summary>Owns frozen numeric input pages for one layout run.</summary>
internal sealed class UILayoutSnapshot
{
    private const int MaxLayoutNodes = 65536;
    private const int ShapedTextBytesPerUtf16 = 2048;
    internal const long MaxWorkingBytes = 32L * 1024 * 1024;
    public UILayoutValuePageView<UILayoutNodeInput> Nodes { get; }
    public UILayoutChildInput[] Children { get; }
    public UIElementIdentity[] Identities { get; }
    public UILayoutValuePageView<UIChangeRevisions> Revisions { get; }
    public UILayoutValuePageView<uint> MeasureVersions { get; }
    public UILayoutValuePageView<uint> ArrangeVersions { get; }
    public BoundingRectangleF RootBounds { get; }
    internal long FontGeneration { get; }
    internal long ShapingBackendGeneration { get; }
    public long EstimatedWorkingBytes { get; }
    private readonly Dictionary<UIElementIdentity, int> _indices;
    private readonly int[] _textIndices;
    private readonly int[] _ownerSyncIndices;
    internal int[] ValidationIndices { get; }
    private readonly HashSet<int> _dirtyIndices;
    internal bool IsDirty(int index) => _dirtyIndices.Contains(index);
    internal bool IsDirtySince(UILayoutSnapshot previous, int index)
        => index >= previous.Nodes.Length ||
            FontGeneration != previous.FontGeneration ||
            ShapingBackendGeneration != previous.ShapingBackendGeneration ||
            Identities[index] != previous.Identities[index] ||
            MeasureVersions[index] != previous.MeasureVersions[index] ||
            ArrangeVersions[index] != previous.ArrangeVersions[index] ||
            Revisions[index].Measure != previous.Revisions[index].Measure ||
            Revisions[index].Arrange != previous.Revisions[index].Arrange ||
            Revisions[index].Transform != previous.Revisions[index].Transform ||
            Revisions[index].Topology != previous.Revisions[index].Topology ||
            Revisions[index].Text != previous.Revisions[index].Text ||
            !Nodes[index].Equals(previous.Nodes[index]);
    internal ReadOnlySpan<int> OwnerSyncIndices => _ownerSyncIndices;
    internal ReadOnlySpan<int> TextIndices => _textIndices;

    /// <summary>Resolve frozen face requests on the bounded layout worker.</summary>
    internal UILayoutSnapshot ResolveShaping(IUITextWorkerShapingBackend backend,
        UILayoutSnapshot? previousRaw, UILayoutSnapshot? previousResolved,
        CancellationToken cancellation)
    {
        UILayoutValuePageBuilder<UILayoutNodeInput>? changed = null;
        foreach (int index in _textIndices)
        {
            cancellation.ThrowIfCancellationRequested();
            UITextLayoutInput? raw = Nodes[index].TextInput;
            if (raw?.ShapingRequest is not { } request)
                continue;
            if (previousRaw is not null && previousResolved is not null &&
                ReferenceEquals(Identities, previousRaw.Identities) &&
                SharesTopologyMetadataWith(previousRaw) &&
                Nodes.SharesPageWith(previousRaw.Nodes, index) &&
                index < previousResolved.Nodes.Length &&
                previousResolved.Nodes[index].TextInput?.ShapedRun is not null)
            {
                changed ??= new UILayoutValuePageBuilder<UILayoutNodeInput>(Nodes);
                changed.SharePage(index, previousResolved.Nodes);
                continue;
            }
            UITextShapedRunInput resolved;
            if (previousRaw is not null && previousResolved is not null &&
                index < previousRaw.Nodes.Length && index < previousResolved.Nodes.Length &&
                Identities[index] == previousRaw.Identities[index] &&
                Identities[index] == previousResolved.Identities[index] &&
                Equals(previousRaw.Nodes[index].TextInput, raw) &&
                previousResolved.Nodes[index].TextInput?.ShapedRun is { } cached)
                resolved = cached;
            else
            {
                long scratch = backend.MaxScratchBytes(request);
                if (scratch < 0 || scratch > MaxWorkingBytes - EstimatedWorkingBytes)
                    throw new InvalidOperationException("Canvas shaping exceeds the 32 MiB working-page budget.");
                UITextShapingResult shaped = backend.Shape(request);
                if (!ReferenceEquals(shaped.Request, request))
                    throw new InvalidOperationException("UI.ShapingRequestMismatch: the backend returned another request.");
                resolved = new UITextShapedRunInput(shaped, raw.ShapingFeatureSource);
            }
            changed ??= new UILayoutValuePageBuilder<UILayoutNodeInput>(Nodes);
            changed.Edit(index) = Nodes[index] with
            {
                TextInput = raw with { ShapedRun = resolved, ShapingRequest = null }
            };
        }
        if (changed is null)
            return this;
        return new UILayoutSnapshot(changed.Freeze(), Children, Identities,
            Revisions, MeasureVersions, ArrangeVersions, RootBounds,
            this, EstimatedWorkingBytes, ValidationIndices, FontGeneration,
            ShapingBackendGeneration);
    }

    internal bool SharesTopologyMetadataWith(UILayoutSnapshot other)
        => ReferenceEquals(_indices, other._indices) &&
            ReferenceEquals(_textIndices, other._textIndices) &&
            ReferenceEquals(_ownerSyncIndices, other._ownerSyncIndices);

    private UILayoutSnapshot(
        UILayoutValuePageView<UILayoutNodeInput> nodes, UILayoutChildInput[] children,
        UIElementIdentity[] identities, UILayoutValuePageView<UIChangeRevisions> revisions,
        UILayoutValuePageView<uint> measureVersions, UILayoutValuePageView<uint> arrangeVersions,
        BoundingRectangleF rootBounds, UILayoutSnapshot? stableTopology = null,
        long? workingBytes = null, int[]? validationIndices = null,
        long? fontGeneration = null, long? shapingBackendGeneration = null)
    {
        Nodes = nodes;
        Children = children;
        Identities = identities;
        Revisions = revisions;
        MeasureVersions = measureVersions;
        ArrangeVersions = arrangeVersions;
        RootBounds = rootBounds;
        FontGeneration = fontGeneration ?? FontGlyphSet.GlobalLayoutMetricsGeneration;
        ShapingBackendGeneration = shapingBackendGeneration ?? UITextShapingBackendRegistry.Generation;
        ValidationIndices = validationIndices ?? CreateAllIndices(nodes.Length);
        _dirtyIndices = new HashSet<int>(ValidationIndices);
        if (stableTopology is not null)
        {
            _indices = stableTopology._indices;
            _textIndices = stableTopology._textIndices;
            _ownerSyncIndices = stableTopology._ownerSyncIndices;
        }
        else
        {
            _indices = new Dictionary<UIElementIdentity, int>(identities.Length);
            List<int> textIndices = [];
            List<int> syncIndices = [];
            for (int i = 0; i < nodes.Length; i++)
            {
                _indices.Add(identities[i], i);
                if (nodes[i].TextInput is not null)
                    textIndices.Add(i);
                if (NeedsOwnerSynchronization(nodes[i]))
                    syncIndices.Add(i);
            }
            _textIndices = [.. textIndices];
            _ownerSyncIndices = [.. syncIndices];
        }
        EstimatedWorkingBytes = workingBytes ?? EstimateWorkingBytes(nodes, children);
        if (EstimatedWorkingBytes > MaxWorkingBytes)
            throw new InvalidOperationException("Canvas layout exceeds the 32 MiB working-page budget.");
    }

    private static bool NeedsOwnerSynchronization(in UILayoutNodeInput node)
        => node.Operator is UILayoutOperator.Scrollable or UILayoutOperator.DockingRoot ||
            node.Operator == UILayoutOperator.List && (node.OperatorFlags & 16) != 0;

    private static int[] CreateAllIndices(int count)
    {
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
            indices[i] = i;
        return indices;
    }

    private static long EstimateWorkingBytes(UILayoutValuePageView<UILayoutNodeInput> nodes, UILayoutChildInput[] children)
    {
        // Includes descriptor and version arrays, owner target references, output scratch,
        // and conservative per-glyph text result storage. Accepted packets own their pages.
        long bytes = (long)nodes.Length * 512 + (long)children.Length * 32;
        for (int i = 0; i < nodes.Length; i++)
        {
            bytes += PayloadBytes(nodes[i]);
            if (bytes > MaxWorkingBytes)
                return bytes;
        }
        return bytes;
    }

    private static long PayloadBytes(in UILayoutNodeInput node)
    {
        long bytes = ((long)node.GridRows.Length + node.GridColumns.Length) * 48;
        if (node.TextInput is { } text)
            bytes += (long)text.Text.Length * (text.ShapingRequest is not null ? ShapedTextBytesPerUtf16 : 512) +
                (long)text.Runes.Count * 256 + (long)(text.ShapingFeatureSource?.Length ?? 0) * 4 +
                (long)(text.ShapingRequest?.Language.Length ?? 0) * 4;
        return bytes;
    }

    private static void Reserve(ref long estimatedBytes, long bytes)
    {
        if (bytes > MaxWorkingBytes - estimatedBytes)
            throw new InvalidOperationException("Canvas layout exceeds the 32 MiB working-page budget.");
        estimatedBytes += bytes;
    }

    /// <summary>Capture live state on the canvas owner thread.</summary>
    public static UILayoutSnapshot Capture(UICanvasTransform canvas, List<UITransform> targets)
    {
        canvas.ChangeJournal.VerifyMutationAccess();
        return CaptureRoot(canvas,
            new Vector2(canvas.Width ?? float.PositiveInfinity, canvas.Height ?? float.PositiveInfinity),
            targets);
    }

    /// <summary>Capture a detached tree for bounded serial measurement only.</summary>
    internal static UILayoutSnapshot CaptureRoot(UITransform root, Vector2 availableSize,
        List<UITransform> targets)
    {
        long fontGeneration = FontGlyphSet.GlobalLayoutMetricsGeneration;
        long shapingBackendGeneration = UITextShapingBackendRegistry.Generation;
        List<UILayoutNodeInput> nodes = [];
        List<UILayoutChildInput> children = [];
        List<UIElementIdentity> identities = [];
        List<UIChangeRevisions> revisions = [];
        List<uint> measureVersions = [];
        List<uint> arrangeVersions = [];
        long estimatedBytes = 0;
        CaptureNode(root, -1, targets, nodes, children, identities, revisions,
            measureVersions, arrangeVersions, ref estimatedBytes);
        BoundingRectangleF rootConstraint = new(Vector2.Zero, availableSize);
        return new UILayoutSnapshot(UILayoutValuePageView<UILayoutNodeInput>.CopyFrom(nodes.ToArray()),
            children.ToArray(), identities.ToArray(),
            UILayoutValuePageView<UIChangeRevisions>.CopyFrom(revisions.ToArray()),
            UILayoutValuePageView<uint>.CopyFrom(measureVersions.ToArray()),
            UILayoutValuePageView<uint>.CopyFrom(arrangeVersions.ToArray()), rootConstraint,
            workingBytes: estimatedBytes, fontGeneration: fontGeneration,
            shapingBackendGeneration: shapingBackendGeneration);
    }

    /// <summary>Reuse frozen topology and unchanged value pages when the owner tree is stable.</summary>
    public static UILayoutSnapshot CaptureIncremental(
        UICanvasTransform canvas, UILayoutSnapshot previous, IReadOnlyList<UITransform> previousTargets,
        List<UITransform> targets, IReadOnlyList<UIElementChange>? changes = null,
        bool reuseTargets = false)
    {
        canvas.ChangeJournal.VerifyMutationAccess();
        long fontGeneration = FontGlyphSet.GlobalLayoutMetricsGeneration;
        long shapingBackendGeneration = UITextShapingBackendRegistry.Generation;
        if (previousTargets.Count != previous.Nodes.Length || previousTargets.Count == 0 ||
            !ReferenceEquals(previousTargets[0], canvas))
            return Capture(canvas, targets);

        if (changes is null)
            return CaptureIncrementalFullScan(canvas, previous, previousTargets, targets);
        for (int i = 0; i < changes.Count; i++)
            if (changes[i].IsRemoved || (changes[i].Domains & UIChangeDomain.Topology) != 0)
                return Capture(canvas, targets);

        long beforeSyncRevision = canvas.ChangeJournal.Revision;
        for (int i = 0; i < previous._ownerSyncIndices.Length; i++)
            SynchronizeOwnerState(previousTargets[previous._ownerSyncIndices[i]]);
        if (canvas.ChangeJournal.Revision != beforeSyncRevision)
            return Capture(canvas, targets);

        HashSet<int> dirty = [];
        for (int i = 0; i < changes.Count; i++)
        {
            if (!previous._indices.TryGetValue(changes[i].Element, out int index))
                return Capture(canvas, targets);
            MarkAncestors(index);
            if (previousTargets[index] is UIListTransform or UIScrollableTransform or UIDockingRootTransform)
            {
                SynchronizeOwnerState(previousTargets[index]);
                if (canvas.ChangeJournal.Revision != beforeSyncRevision)
                    return Capture(canvas, targets);
            }
        }
        if (fontGeneration != previous.FontGeneration ||
            shapingBackendGeneration != previous.ShapingBackendGeneration)
        {
            for (int i = 0; i < previous._textIndices.Length; i++)
            {
                int index = previous._textIndices[i];
                UITextLayoutInput? oldText = previous.Nodes[index].TextInput;
                if (!Equals(previousTargets[index].SceneNode?.GetComponent<UITextComponent>()?.CaptureLayoutInput(), oldText))
                    MarkAncestors(index);
            }
        }
        if (dirty.Count == 0)
        {
            if (!reuseTargets)
                targets.AddRange(previousTargets);
            BoundingRectangleF unchangedRoot = new(Vector2.Zero,
                new Vector2(canvas.Width ?? float.PositiveInfinity, canvas.Height ?? float.PositiveInfinity));
            return new UILayoutSnapshot(previous.Nodes, previous.Children, previous.Identities,
                previous.Revisions, previous.MeasureVersions, previous.ArrangeVersions,
                unchangedRoot, previous, previous.EstimatedWorkingBytes, [0], fontGeneration,
                shapingBackendGeneration);
        }

        UILayoutChildInput[] changedEdges = previous.Children;
        UILayoutValuePageBuilder<UILayoutNodeInput> changedNodes = new(previous.Nodes);
        UILayoutValuePageBuilder<UIChangeRevisions> changedRevisions = new(previous.Revisions);
        UILayoutValuePageBuilder<uint> changedMeasureVersions = new(previous.MeasureVersions);
        UILayoutValuePageBuilder<uint> changedArrangeVersions = new(previous.ArrangeVersions);
        long workingBytes = previous.EstimatedWorkingBytes;
        bool stableMetadata = true;
        foreach (int index in dirty)
        {
            UITransform target = previousTargets[index];
            UILayoutNodeInput old = previous.Nodes[index];
            if (target.ElementIdentity != previous.Identities[index] ||
                (old.Parent < 0 ? !ReferenceEquals(target, canvas) :
                    !ReferenceEquals(target.Parent, previousTargets[old.Parent])))
                return Capture(canvas, targets);
            int ordinal = 0;
            for (int c = 0; c < target.Children.Count; c++)
            {
                if (target.Children[c] is not UITransform child)
                    continue;
                if (ordinal >= old.ChildCount)
                    return Capture(canvas, targets);
                int edgeIndex = old.FirstChild + ordinal++;
                int childIndex = changedEdges[edgeIndex].NodeIndex;
                if (childIndex >= previousTargets.Count || !ReferenceEquals(child, previousTargets[childIndex]))
                    return Capture(canvas, targets);
                UILayoutChildInput edge = CaptureEdge(target, child, childIndex);
                if (edge != previous.Children[edgeIndex])
                {
                    if (ReferenceEquals(changedEdges, previous.Children))
                        changedEdges = (UILayoutChildInput[])previous.Children.Clone();
                    changedEdges[edgeIndex] = edge;
                }
            }
            if (ordinal != old.ChildCount)
                return Capture(canvas, targets);
            UIChangeRevisions revision = target.ChangeRevisions;
            uint measure = target.LayoutMeasureVersion;
            uint arrange = target.LayoutArrangeVersion;
            bool textChanged = old.TextInput is { } oldText &&
                !Equals(target.SceneNode?.GetComponent<UITextComponent>()?.CaptureLayoutInput(), oldText);
            if (revision != previous.Revisions[index] || measure != previous.MeasureVersions[index] ||
                arrange != previous.ArrangeVersions[index] || textChanged ||
                EdgesChanged(old, previous.Children, changedEdges))
            {
                PreflightTextCapture(target, workingBytes, old);
                workingBytes -= PayloadBytes(old);
                changedNodes.Edit(index) = CaptureValues(target, ResolveOperator(target), old.Parent,
                    old.FirstChild, old.ChildCount, ref workingBytes);
                if ((old.TextInput is null) != (changedNodes.Edit(index).TextInput is null) ||
                    NeedsOwnerSynchronization(old) != NeedsOwnerSynchronization(changedNodes.Edit(index)))
                    stableMetadata = false;
            }
            if (revision != previous.Revisions[index])
                changedRevisions.Edit(index) = revision;
            if (measure != previous.MeasureVersions[index])
                changedMeasureVersions.Edit(index) = measure;
            if (arrange != previous.ArrangeVersions[index])
                changedArrangeVersions.Edit(index) = arrange;
        }
        if (!reuseTargets)
            targets.AddRange(previousTargets);
        BoundingRectangleF changedRoot = new(Vector2.Zero,
            new Vector2(canvas.Width ?? float.PositiveInfinity, canvas.Height ?? float.PositiveInfinity));
        return new UILayoutSnapshot(changedNodes.Freeze(), changedEdges, previous.Identities,
            changedRevisions.Freeze(), changedMeasureVersions.Freeze(), changedArrangeVersions.Freeze(),
            changedRoot, stableMetadata ? previous : null, workingBytes, [.. dirty], fontGeneration,
            shapingBackendGeneration);

        void MarkAncestors(int index)
        {
            while (index >= 0 && dirty.Add(index))
                index = previous.Nodes[index].Parent;
        }
    }

    private static UILayoutSnapshot CaptureIncrementalFullScan(
        UICanvasTransform canvas, UILayoutSnapshot previous, IReadOnlyList<UITransform> previousTargets,
        List<UITransform> targets)
    {
        // Callers without a change journal use the explicit full capture path.
        return Capture(canvas, targets);
    }

    private static void PreflightTextCapture(UITransform target, long estimatedBytes, in UILayoutNodeInput old)
    {
        if (old.TextInput is null)
            return;
        UITextComponent? text = target.SceneNode?.GetComponent<UITextComponent>();
        if (text is null)
            return;
        FontGlyphSet font = text.Font ?? FontGlyphSet.LoadDefaultUIFont();
        long candidateBytes = estimatedBytes - PayloadBytes(old);
        Reserve(ref candidateBytes, (long)(text.Text?.Length ?? 0) *
            (text.UseShapedText ? ShapedTextBytesPerUtf16 : 512) +
            (long)font.LayoutGlyphCount * 256 +
            (long)(text.OpenTypeFeatures?.Length ?? 0) * 4 +
            (text.UseShapedText ? (long)text.ShapeLanguage.Length * 4 : 0));
    }

    private static bool EdgesChanged(UILayoutNodeInput node, UILayoutChildInput[] before, UILayoutChildInput[] now)
    {
        for (int i = 0; i < node.ChildCount; i++)
            if (before[node.FirstChild + i] != now[node.FirstChild + i])
                return true;
        return false;
    }

    private static UILayoutChildInput CaptureEdge(UITransform parent, UITransform child, int childIndex)
    {
        int row = 0, column = 0, rowSpan = 1, columnSpan = 1;
        if (child.PlacementInfo is UIGridTransform.UIGridChildPlacementInfo grid)
        {
            row = grid.Row;
            column = grid.Column;
            rowSpan = grid.RowSpan;
            columnSpan = grid.ColumnSpan;
        }
        else if (parent is UIListTransform list && list.VirtualProvider is not null &&
            child is UIBoundableTransform rowTransform && list.TryGetVirtualRowIndex(rowTransform, out int rowIndex))
            row = rowIndex;
        return new(childIndex, row, column, rowSpan, columnSpan);
    }

    private static int CaptureNode(
        UITransform transform, int parent, List<UITransform> targets,
        List<UILayoutNodeInput> nodes, List<UILayoutChildInput> children,
        List<UIElementIdentity> identities, List<UIChangeRevisions> revisions,
        List<uint> measureVersions, List<uint> arrangeVersions, ref long estimatedBytes)
    {
        if (nodes.Count >= MaxLayoutNodes)
            throw new InvalidOperationException("Canvas layout exceeds the 65536-node snapshot limit.");
        Reserve(ref estimatedBytes, 512);
        int index = nodes.Count;
        UILayoutOperator op = ResolveOperator(transform);
        SynchronizeOwnerState(transform);
        targets.Add(transform);
        nodes.Add(default);
        identities.Add(transform.ElementIdentity);
        revisions.Add(transform.ChangeRevisions);
        measureVersions.Add(transform.LayoutMeasureVersion);
        arrangeVersions.Add(transform.LayoutArrangeVersion);

        int firstChild = children.Count;
        int count = 0;
        for (int i = 0; i < transform.Children.Count; i++)
            if (transform.Children[i] is UITransform)
                count++;
        Reserve(ref estimatedBytes, (long)count * 32);
        for (int i = 0; i < count; i++)
            children.Add(default);

        int ordinal = 0;
        for (int i = 0; i < transform.Children.Count; i++)
        {
            if (transform.Children[i] is not UITransform child)
                continue;
            int childIndex = CaptureNode(child, index, targets, nodes, children, identities, revisions,
                measureVersions, arrangeVersions, ref estimatedBytes);
            children[firstChild + ordinal++] = CaptureEdge(transform, child, childIndex);
        }

        nodes[index] = CaptureValues(transform, op, parent, firstChild, count, ref estimatedBytes);
        return index;
    }

    private static void SynchronizeOwnerState(UITransform transform)
    {
        if (transform is UIDockingRootTransform dockingRoot && dockingRoot.SceneNode is { } rootNode)
        {
            while (rootNode.Transform.Children.Count < 4)
            {
                var childNode = rootNode.NewChild();
                var childTransform = childNode.SetTransform<UIBoundableTransform>();
                childTransform.Name = $"DockingRootChild{rootNode.Transform.Children.Count - 1}";
                childTransform.MinAnchor = Vector2.Zero;
                childTransform.MaxAnchor = Vector2.One;
            }
        }
        if (transform is UIScrollableTransform scroll)
        {
            Vector2 viewport = scroll.ActualSize - new Vector2(scroll.Padding.X + scroll.Padding.Z,
                scroll.Padding.Y + scroll.Padding.W);
            for (int c = 0; c < scroll.Children.Count; c++)
                if (scroll.Children[c] is UIListTransform list && list.VirtualProvider is not null)
                    list.SynchronizeVirtualRows(list.DisplayHorizontal ? viewport.X : viewport.Y);
            scroll.SynchronizeScroll(viewport);
            for (int c = 0; c < scroll.Children.Count; c++)
                if (scroll.Children[c] is UIListTransform list && list.VirtualProvider is not null)
                {
                    list.ContentScrollOffset = list.DisplayHorizontal ? scroll.ScrollOffset.X : scroll.ScrollOffset.Y;
                    list.SynchronizeVirtualRows(list.DisplayHorizontal ? viewport.X : viewport.Y);
                }
        }
        if (transform is UIListTransform virtualList && virtualList.VirtualProvider is not null &&
            transform.Parent is not UIScrollableTransform)
            virtualList.SynchronizeVirtualRows(virtualList.DisplayHorizontal ? virtualList.ActualSize.X : virtualList.ActualSize.Y);
    }

    private static UILayoutNodeInput CaptureValues(
        UITransform transform, UILayoutOperator op, int parent, int firstChild, int count,
        ref long estimatedBytes)
    {
        float? width = null, height = null, minWidth = null, minHeight = null, maxWidth = null, maxHeight = null;
        float? autoWidth = null, autoHeight = null;
        Vector2 minAnchor = Vector2.Zero, maxAnchor = Vector2.One, pivot = Vector2.Zero;
        Vector4 margins = Vector4.Zero, padding = Vector4.Zero, values = Vector4.Zero;
        float extra = 0;
        int flags = 0;
        bool excludedWidth = false, excludedHeight = false;
        ReadOnlyMemory<UILayoutGridTrack> rows = default, columns = default;
        UITextComponent? textComponent = transform.SceneNode?.GetComponent<UITextComponent>();
        if (textComponent is not null)
        {
            FontGlyphSet font = textComponent.Font ?? FontGlyphSet.LoadDefaultUIFont();
            Reserve(ref estimatedBytes, (long)(textComponent.Text?.Length ?? 0) *
                (textComponent.UseShapedText ? ShapedTextBytesPerUtf16 : 512) +
                (long)font.LayoutGlyphCount * 256 +
                (long)(textComponent.OpenTypeFeatures?.Length ?? 0) * 4 +
                (textComponent.UseShapedText ? (long)textComponent.ShapeLanguage.Length * 4 : 0));
        }
        UITextLayoutInput? textInput = textComponent?.CaptureLayoutInput();
        if (transform is UIBoundableTransform bounded)
        {
            width = bounded.Width;
            height = bounded.Height;
            minWidth = bounded.MinWidth;
            minHeight = bounded.MinHeight;
            maxWidth = bounded.MaxWidth;
            maxHeight = bounded.MaxHeight;
            minAnchor = bounded.MinAnchor;
            maxAnchor = bounded.MaxAnchor;
            pivot = bounded.NormalizedPivot;
            margins = bounded.Margins;
            padding = bounded.Padding;
            excludedWidth = bounded.ExcludeFromParentAutoCalcWidth;
            excludedHeight = bounded.ExcludeFromParentAutoCalcHeight;
            // Arbitrary callbacks do not declare their dependency set. They require a value adapter.
            bool widthCallback = !width.HasValue && bounded.CalcAutoWidthCallback is not null;
            bool heightCallback = !height.HasValue && bounded.CalcAutoHeightCallback is not null;
            if ((widthCallback || heightCallback) && (textInput is null ||
                (widthCallback && bounded.CalcAutoWidthCallback!.Method.DeclaringType != typeof(UITextComponent)) ||
                (heightCallback && bounded.CalcAutoHeightCallback!.Method.DeclaringType != typeof(UITextComponent))))
                throw new NotSupportedException($"Worker layout needs a value adapter for {transform.GetType().FullName} auto sizing.");
        }

        switch (transform)
        {
            case UIListTransform list:
                bool provider = list.VirtualProvider is not null;
                values = new(list.ItemSize ?? float.NaN, list.ItemSpacing,
                    provider ? list.VirtualScrollOffset : list.ContentScrollOffset, list.VirtualContentExtent);
                flags = (list.DisplayHorizontal ? 1 : 0) | ((int)list.ItemAlignment << 1) |
                    (list.Virtual ? 8 : 0) | (provider ? 16 : 0);
                break;
            case UIGridTransform grid:
                Reserve(ref estimatedBytes, ((long)grid.Rows.Count + grid.Columns.Count) * 48);
                rows = CaptureTracks(grid.Rows);
                columns = CaptureTracks(grid.Columns);
                flags = grid.InvertY ? 1 : 0;
                break;
            case UIDualSplitTransform split:
                values = new(split.SplitPercent, split.FixedSize ?? float.NaN, split.SplitterSize, 0);
                flags = (split.VerticalSplit ? 1 : 0) | (split.FirstFixedSize.HasValue ? (split.FirstFixedSize.Value ? 2 : 4) : 0);
                break;
            case UIMultiSplitTransform split:
                values = new(split.SplitPercentFirst, split.SplitPercentSecond,
                    split.FixedSizeFirst ?? float.NaN, split.FixedSizeSecond ?? float.NaN);
                extra = split.SplitterSize;
                flags = (int)split.Arrangement;
                break;
            case UIDockingRootTransform docking:
                values = new(docking.LeftSizeWidth, docking.RightSizeWidth, docking.BottomSizeHeight, 0);
                break;
            case UITabTransform tab:
                flags = tab.SelectedIndex;
                break;
            case UIFittedTransform fitted:
                flags = (int)fitted.FitType;
                break;
            case UIScrollableTransform scroll:
                values = new(scroll.ScrollOffset.X, scroll.ScrollOffset.Y, 0, 0);
                break;
        }
        if (transform is IUILayoutValueAdapter adapter)
        {
            UILayoutValuePayload payload = adapter.CaptureLayoutPayload();
            values = payload.Values;
            extra = payload.Extra;
            flags = payload.Flags;
        }

        return new UILayoutNodeInput(op, parent, firstChild, count,
            width, height, minWidth, minHeight, maxWidth, maxHeight,
            minAnchor, maxAnchor, pivot, transform.Translation, margins, padding,
            transform.IsCollapsed, transform.IsHidden, excludedWidth, excludedHeight,
            autoWidth, autoHeight, values, extra, flags, rows, columns, textInput);
    }

    private static UILayoutGridTrack[] CaptureTracks(IReadOnlyList<UISizingDefinition> definitions)
    {
        UILayoutGridTrack[] tracks = new UILayoutGridTrack[definitions.Count];
        for (int i = 0; i < tracks.Length; i++)
        {
            UISizingDefinition track = definitions[i];
            tracks[i] = new UILayoutGridTrack(track.Value.Mode, track.Value.Value,
                track.Min?.Mode, track.Min?.Value ?? 0,
                track.Max?.Mode, track.Max?.Value ?? 0);
        }
        return tracks;
    }

    private static UILayoutOperator ResolveOperator(UITransform transform)
    {
        Type type = transform.GetType();
        if (type == typeof(UICanvasTransform)) return UILayoutOperator.Canvas;
        if (type == typeof(UIListTransform)) return UILayoutOperator.List;
        if (type == typeof(UIGridTransform) || type == typeof(UITreeTransform)) return UILayoutOperator.Grid;
        if (type == typeof(UIDualSplitTransform)) return UILayoutOperator.DualSplit;
        if (type == typeof(UIMultiSplitTransform)) return UILayoutOperator.MultiSplit;
        if (type == typeof(UIDockingRootTransform)) return UILayoutOperator.DockingRoot;
        if (type == typeof(UITabTransform)) return UILayoutOperator.Tab;
        if (type == typeof(UIFittedTransform)) return UILayoutOperator.Fitted;
        if (type == typeof(UIScrollableTransform)) return UILayoutOperator.Scrollable;
        if (type == typeof(UIRotationTransform)) return UILayoutOperator.Rotation;
        if (type == typeof(UIBoundableTransform) || type == typeof(UIDockableTransform) ||
            type == typeof(UITreeItemTransform))
            return UILayoutOperator.Boundable;
        if (type == typeof(UITransform))
            return UILayoutOperator.Transform;
        if (transform is IUILayoutValueAdapter adapter &&
            adapter.LayoutOperator is UILayoutOperator.Transform or UILayoutOperator.Boundable)
            return adapter.LayoutOperator;
        throw new NotSupportedException($"Worker layout has no value operator for {type.FullName}.");
    }
}
