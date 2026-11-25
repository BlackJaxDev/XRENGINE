namespace XREngine.Rendering.UI;

/// <summary>Reads either a contiguous hit set or one retained publication.</summary>
internal ref struct UIHitRecordView
{
    private readonly ReadOnlySpan<UIHitRecord> _records;
    private readonly UIPublication? _publication;
    private ReadOnlySpan<UIHitRecord> _page;
    private int _pageStart;

    internal UIHitRecordView(ReadOnlySpan<UIHitRecord> records)
    {
        _records = records;
        _publication = null;
        _page = default;
        _pageStart = -1;
    }

    internal UIHitRecordView(UIPublication publication)
    {
        _records = default;
        _publication = publication;
        _page = default;
        _pageStart = -1;
    }

    internal int Length => _publication?.HitRecordCount ?? _records.Length;
    internal UIHitRecord this[int index]
    {
        get
        {
            if (_publication is not { } publication)
                return _records[index];
            if ((uint)index >= (uint)publication.HitRecordCount)
                throw new IndexOutOfRangeException();
            if (_pageStart < 0 || index < _pageStart || index >= _pageStart + _page.Length)
            {
                UIRecordStore<UIHitRecord> store = publication.HitRecordStore;
                int pageIndex = store.LocatePage(index, out int offset);
                _page = store.GetPage(pageIndex);
                _pageStart = index - offset;
            }
            return _page[index - _pageStart];
        }
    }
}
