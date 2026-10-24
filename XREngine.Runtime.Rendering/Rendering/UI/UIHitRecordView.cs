namespace XREngine.Rendering.UI;

/// <summary>Reads either a contiguous hit set or one retained publication.</summary>
internal ref struct UIHitRecordView
{
    private readonly ReadOnlySpan<UIHitRecord> _records;
    private readonly UIPublication? _publication;
    private ReadOnlySpan<UIHitRecord> _page;
    private int _pageIndex;

    internal UIHitRecordView(ReadOnlySpan<UIHitRecord> records)
    {
        _records = records;
        _publication = null;
        _page = default;
        _pageIndex = -1;
    }

    internal UIHitRecordView(UIPublication publication)
    {
        _records = default;
        _publication = publication;
        _page = default;
        _pageIndex = -1;
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
            int pageIndex = index / UIRecordStore<UIHitRecord>.PageSize;
            if (pageIndex != _pageIndex)
            {
                _page = publication.GetHitRecordPage(pageIndex);
                _pageIndex = pageIndex;
            }
            return _page[index % UIRecordStore<UIHitRecord>.PageSize];
        }
    }
}
