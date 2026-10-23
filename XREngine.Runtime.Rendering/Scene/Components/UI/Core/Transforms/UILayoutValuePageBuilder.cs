namespace XREngine.Rendering.UI;

/// <summary>Copies only value pages that an owner capture changes.</summary>
internal sealed class UILayoutValuePageBuilder<T>
{
    private readonly T[][] _pages;
    private readonly bool[] _owned;
    private readonly int _length;
    private bool _frozen;

    internal UILayoutValuePageBuilder(UILayoutValuePageView<T> previous)
    {
        _length = previous.Length;
        _pages = previous.CopyPageTable();
        _owned = new bool[_pages.Length];
    }

    internal ref T Edit(int index)
    {
        if (_frozen || (uint)index >= (uint)_length)
            throw new InvalidOperationException("Layout input page is frozen or index is invalid.");
        int page = index / UILayoutValuePageView<T>.PageSize;
        if (!_owned[page])
        {
            _pages[page] = (T[])_pages[page].Clone();
            _owned[page] = true;
        }
        return ref _pages[page][index % UILayoutValuePageView<T>.PageSize];
    }

    internal UILayoutValuePageView<T> Freeze()
    {
        _frozen = true;
        return new UILayoutValuePageView<T>(_pages, _length);
    }
}
