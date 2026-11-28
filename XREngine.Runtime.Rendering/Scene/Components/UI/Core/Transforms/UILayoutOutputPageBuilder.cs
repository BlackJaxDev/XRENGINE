namespace XREngine.Rendering.UI;

/// <summary>Writes changed output pages and shares untouched accepted pages.</summary>
internal sealed class UILayoutOutputPageBuilder
{
    private readonly UILayoutNodeOutput[][] _pages;
    private readonly bool[] _owned;
    // One flag for each node. A set flag means that the measure pass changed the sizes of the node or of a node
    // below it, so the arrange pass must not reuse the accepted arrangement of that subtree.
    private readonly bool[] _measureChanged;
    private bool _frozen;
    private int _copiedNodes;
    private int _visitedNodes;

    internal UILayoutOutputPageBuilder(int length, UILayoutOutputPageView? previous = null)
    {
        Length = length;
        if (previous is not null && previous.Length != length)
            previous = null;
        _pages = previous?.CopyPageTable() ?? new UILayoutNodeOutput[(length + UILayoutOutputPageView.PageSize - 1) /
            UILayoutOutputPageView.PageSize][];
        _owned = new bool[_pages.Length];
        _measureChanged = new bool[length];
    }

    internal int Length { get; }

    internal ref readonly UILayoutNodeOutput this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            UILayoutNodeOutput[]? page = _pages[index / UILayoutOutputPageView.PageSize];
            if (page is null)
                throw new InvalidOperationException("Layout output was read before measurement.");
            return ref page[index % UILayoutOutputPageView.PageSize];
        }
    }

    internal ref UILayoutNodeOutput Edit(int index)
    {
        if (_frozen || (uint)index >= (uint)Length)
            throw new InvalidOperationException("Layout output page is frozen or index is invalid.");
        int pageIndex = index / UILayoutOutputPageView.PageSize;
        if (!_owned[pageIndex])
        {
            int count = Math.Min(UILayoutOutputPageView.PageSize,
                Length - pageIndex * UILayoutOutputPageView.PageSize);
            bool copied = _pages[pageIndex] is not null;
            _pages[pageIndex] = _pages[pageIndex] is { } retained
                ? (UILayoutNodeOutput[])retained.Clone()
                : new UILayoutNodeOutput[count];
            if (_pages[pageIndex].Length != count)
                throw new InvalidOperationException("Layout output page length changed.");
            if (copied)
                _copiedNodes += count;
            _owned[pageIndex] = true;
        }
        return ref _pages[pageIndex][index % UILayoutOutputPageView.PageSize];
    }

    internal UILayoutOutputPageView Freeze()
    {
        _frozen = true;
        return new UILayoutOutputPageView(_pages, Length, _owned, _copiedNodes, _visitedNodes);
    }

    internal void RecordVisit() => _visitedNodes++;

    /// <summary>Records that the measure pass changed the sizes of this node or of a node in its subtree.</summary>
    internal void MarkMeasureChanged(int index) => _measureChanged[index] = true;

    /// <summary>Gets whether the measure pass changed the sizes of this node or of a node in its subtree.</summary>
    internal bool IsMeasureChanged(int index) => _measureChanged[index];

    internal void ValidateChangedPages()
    {
        for (int page = 0; page < _owned.Length; page++)
        {
            if (!_owned[page])
                continue;
            int start = page * UILayoutOutputPageView.PageSize;
            int end = Math.Min(start + UILayoutOutputPageView.PageSize, Length);
            for (int i = start; i < end; i++)
                UILayoutValueKernel.ValidateOutput(i, _pages[page][i - start]);
        }
    }
}
