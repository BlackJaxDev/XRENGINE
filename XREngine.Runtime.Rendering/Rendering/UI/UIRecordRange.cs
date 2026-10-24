using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Owns the exact record leaves that intersect one ordered range.</summary>
internal sealed class UIRecordRange<T> where T : struct
{
    private static readonly Action<UIRecordLeafClaim<T>> RetainLeaf =
        static claim => claim.Leaf.Retain();
    private static readonly Action<UIRecordLeafClaim<T>> ReleaseLeaf =
        static claim => claim.Leaf.Release();
    private readonly UIRecordStore<UIRecordLeafClaim<T>> _leafStore;
    private readonly int _firstPage;
    private readonly int _offset;
    private int _retired;

    private UIRecordRange(UIRecordStore<UIRecordLeafClaim<T>> leafStore,
        int firstPage, int offset, int length)
    {
        _leafStore = leafStore;
        _firstPage = firstPage;
        _offset = offset;
        Length = length;
    }

    internal int Length { get; }
    internal int Offset => _offset;
    internal int PageCount => _leafStore.Length;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;

    internal static UIRecordRange<T> Create(UIRecordStore<T> store, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (offset < 0 || length < 0 || offset > store.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int firstPage = offset / UIRecordStore<T>.PageSize;
        int endPage = length == 0 ? firstPage :
            (offset + length - 1) / UIRecordStore<T>.PageSize + 1;
        UIRecordLeafClaim<T>[] leaves = new UIRecordLeafClaim<T>[endPage - firstPage];
        for (int index = 0; index < leaves.Length; index++)
            leaves[index] = new UIRecordLeafClaim<T>(store.GetLeaf(firstPage + index));
        UIRecordStore<UIRecordLeafClaim<T>> leafStore =
            UIRecordStore<UIRecordLeafClaim<T>>.Create(leaves, RetainLeaf, ReleaseLeaf);
        return new UIRecordRange<T>(leafStore, firstPage, offset, length);
    }

    internal UIRecordRange<T> WithReplacedPage(int pageIndex, UIRecordStore<T> records)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if ((uint)pageIndex >= (uint)PageCount ||
            records.Length < _offset + Length)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        int slotPage = pageIndex / UIRecordStore<UIRecordLeafClaim<T>>.PageSize;
        UIRecordLeafClaim<T>[] slots = _leafStore.GetPage(slotPage).ToArray();
        slots[pageIndex % UIRecordStore<UIRecordLeafClaim<T>>.PageSize] =
            new UIRecordLeafClaim<T>(records.GetLeaf(_firstPage + pageIndex));
        UIRecordStore<UIRecordLeafClaim<T>> leafStore =
            _leafStore.WithReplacedPage(slotPage, slots);
        return new UIRecordRange<T>(leafStore, _firstPage, _offset, Length);
    }

    internal T this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(ResourcesRetired, this);
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            int absolute = _offset + index;
            return _leafStore[absolute / UIRecordStore<T>.PageSize - _firstPage]
                .Leaf.Values![absolute % UIRecordStore<T>.PageSize];
        }
    }

    internal ReadOnlySpan<T> GetPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if ((uint)pageIndex >= (uint)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        int absolutePage = _firstPage + pageIndex;
        int start = Math.Max(_offset, absolutePage * UIRecordStore<T>.PageSize);
        int end = Math.Min(_offset + Length,
            (absolutePage + 1) * UIRecordStore<T>.PageSize);
        return _leafStore[pageIndex].Leaf.Values!.AsSpan(
            start - absolutePage * UIRecordStore<T>.PageSize, end - start);
    }

    internal void CopyTo(Span<T> destination)
    {
        if (destination.Length < Length)
            throw new ArgumentException("The destination is shorter than the record range.", nameof(destination));
        int offset = 0;
        for (int page = 0; page < PageCount; page++)
        {
            ReadOnlySpan<T> source = GetPage(page);
            source.CopyTo(destination[offset..]);
            offset += source.Length;
        }
        UIRuntimeCounters.Add(UIWorkCounter.PublicationFlatCellsMaterialized, Length);
    }

    internal void Release()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            throw new InvalidOperationException("A UI record range was released twice.");
        try { ReleaseLeaves(); }
        finally { GC.SuppressFinalize(this); }
    }

    ~UIRecordRange()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;
        try { ReleaseLeaves(); }
        catch (Exception failure)
        {
            try { System.Diagnostics.Trace.TraceError("UI record range final cleanup failed: {0}", failure); }
            catch { }
        }
    }

    private void ReleaseLeaves()
        => _leafStore.Release();
}
