namespace XREngine.Rendering.UI;

/// <summary>Immutable accepted layout values in node order.</summary>
public sealed class UILayoutOutputPageView
{
    internal const int PageSize = 256;
    private readonly UILayoutNodeOutput[][] _pages;

    internal UILayoutOutputPageView(UILayoutNodeOutput[][] pages, int length,
        bool[] changedPages, int copiedNodes, int visitedNodes)
    {
        _pages = pages;
        Length = length;
        ChangedPages = changedPages;
        CopiedNodes = copiedNodes;
        VisitedNodes = visitedNodes;
    }

    public int Length { get; }
    internal bool[] ChangedPages { get; }
    internal int CopiedNodes { get; }
    internal int VisitedNodes { get; }
    internal bool SharesPageWith(UILayoutOutputPageView other, int index)
        => ReferenceEquals(_pages[index / PageSize], other._pages[index / PageSize]);

    public ref readonly UILayoutNodeOutput this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            return ref _pages[index / PageSize][index % PageSize];
        }
    }

    internal UILayoutNodeOutput[][] CopyPageTable() => (UILayoutNodeOutput[][])_pages.Clone();

    internal static UILayoutOutputPageView CopyFrom(ReadOnlySpan<UILayoutNodeOutput> values)
    {
        UILayoutOutputPageBuilder builder = new(values.Length);
        for (int i = 0; i < values.Length; i++)
            builder.Edit(i) = values[i];
        return builder.Freeze();
    }

    internal UILayoutNodeOutput[] ToArray()
    {
        UILayoutNodeOutput[] values = new UILayoutNodeOutput[Length];
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].CopyTo(values, i * PageSize);
        return values;
    }
}
