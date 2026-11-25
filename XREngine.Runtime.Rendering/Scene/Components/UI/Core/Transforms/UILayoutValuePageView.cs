namespace XREngine.Rendering.UI;

/// <summary>Immutable indexed values shared between layout snapshots.</summary>
internal sealed class UILayoutValuePageView<T>
{
    internal const int PageSize = 256;
    private readonly T[][] _pages;

    internal UILayoutValuePageView(T[][] pages, int length)
    {
        _pages = pages;
        Length = length;
    }

    internal int Length { get; }

    internal ref readonly T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            return ref _pages[index / PageSize][index % PageSize];
        }
    }

    internal T[][] CopyPageTable() => (T[][])_pages.Clone();
    internal T[] GetPage(int index) => _pages[index / PageSize];

    internal bool SharesPageWith(UILayoutValuePageView<T> other, int index)
        => ReferenceEquals(_pages[index / PageSize], other._pages[index / PageSize]);

    internal static UILayoutValuePageView<T> CopyFrom(ReadOnlySpan<T> values)
    {
        T[][] pages = new T[(values.Length + PageSize - 1) / PageSize][];
        for (int i = 0; i < pages.Length; i++)
            pages[i] = values.Slice(i * PageSize, Math.Min(PageSize, values.Length - i * PageSize)).ToArray();
        return new UILayoutValuePageView<T>(pages, values.Length);
    }
}
