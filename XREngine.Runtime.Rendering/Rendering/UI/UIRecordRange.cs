using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>
/// Owns one ordered record range. Composition ranges can copy partial boundary
/// leaves. Other ranges keep the source leaf claims.
/// </summary>
internal sealed class UIRecordRange<T> where T : struct
{
    private static readonly Action<UIRecordLeafClaim<T>> RetainLeaf =
        static claim => claim.Leaf.Retain();
    private static readonly Action<UIRecordLeafClaim<T>> ReleaseLeaf =
        static claim => claim.Leaf.Release();
    private readonly UIRecordStore<UIRecordLeafClaim<T>> _leafStore;
    private readonly bool _copyBoundaryRecords;
    // Offset and page lengths in the source store. Boundary leaves own only this range.
    private readonly int _headOffset;
    private readonly int _firstSourcePageLength;
    private readonly int _lastSourcePageLength;
    // Range-relative end of each page slice.
    private readonly int[] _ends;
    private int _retired;

    private UIRecordRange(UIRecordStore<UIRecordLeafClaim<T>> leafStore,
        bool copyBoundaryRecords, int headOffset,
        int firstSourcePageLength, int lastSourcePageLength,
        int[] ends, int length)
    {
        _leafStore = leafStore;
        _copyBoundaryRecords = copyBoundaryRecords;
        _headOffset = headOffset;
        _firstSourcePageLength = firstSourcePageLength;
        _lastSourcePageLength = lastSourcePageLength;
        _ends = ends;
        Length = length;
    }

    internal int Length { get; }
    internal int PageCount => _ends.Length;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;

    internal static UIRecordRange<T> Create(UIRecordStore<T> store, int offset, int length,
        bool copyBoundaryRecords = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (offset < 0 || length < 0 || offset > store.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length == 0)
            return new UIRecordRange<T>(UIRecordStore<UIRecordLeafClaim<T>>.Create([],
                RetainLeaf, ReleaseLeaf), copyBoundaryRecords, 0, 0, 0, [], 0);
        int firstPage = store.LocatePage(offset, out int headOffset);
        int lastPage = store.LocatePage(offset + length - 1, out _);
        UIRecordLeafClaim<T>[] leaves = new UIRecordLeafClaim<T>[lastPage - firstPage + 1];
        int[] ends = new int[leaves.Length];
        int covered = 0;
        UIRecordStoreNode<T>? firstOwned = null;
        UIRecordStoreNode<T>? lastOwned = null;
        UIRecordStore<UIRecordLeafClaim<T>>? leafStore = null;
        try
        {
            for (int index = 0; index < leaves.Length; index++)
            {
                UIRecordStoreNode<T> source = store.GetLeaf(firstPage + index);
                int start = index == 0 ? headOffset : 0;
                int count = Math.Min(source.Values!.Length - start, length - covered);
                UIRecordStoreNode<T> leaf = source;
                if (copyBoundaryRecords && (start != 0 || count != source.Values.Length))
                {
                    leaf = source.CopySlice(start, count);
                    if (index == 0)
                        firstOwned = leaf;
                    else
                        lastOwned = leaf;
                }
                leaves[index] = new UIRecordLeafClaim<T>(leaf);
                covered += count;
                ends[index] = covered;
            }
            leafStore = UIRecordStore<UIRecordLeafClaim<T>>.Create(leaves, RetainLeaf, ReleaseLeaf);
            ReleaseLocalSlices(ref firstOwned, ref lastOwned);
            UIRecordRange<T> range = new(leafStore, copyBoundaryRecords, headOffset,
                store.GetPageLength(firstPage), store.GetPageLength(lastPage), ends, length);
            leafStore = null;
            return range;
        }
        catch (Exception failure)
        {
            List<Exception> cleanup = [];
            try { leafStore?.Release(); } catch (Exception error) { cleanup.Add(error); }
            try { ReleaseLocalSlices(ref firstOwned, ref lastOwned); }
            catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
            {
                cleanup.Insert(0, failure);
                throw new AggregateException("UI record range creation and cleanup failed.", cleanup);
            }
            throw;
        }
    }

    private static void ReleaseLocalSlices(ref UIRecordStoreNode<T>? first,
        ref UIRecordStoreNode<T>? last)
    {
        UIRecordStoreNode<T>? firstOwned = first;
        UIRecordStoreNode<T>? lastOwned = last;
        first = null;
        last = null;
        List<Exception>? failures = null;
        try { firstOwned?.Release(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        try { lastOwned?.Release(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null)
            throw new AggregateException("UI record boundary cleanup failed.", failures);
    }

    /// <summary>Takes a second owner of the same pages. The caller releases each owner once.</summary>
    internal UIRecordRange<T> Share()
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        _leafStore.Retain();
        return new UIRecordRange<T>(_leafStore, _copyBoundaryRecords, _headOffset,
            _firstSourcePageLength, _lastSourcePageLength, _ends, Length);
    }

    /// <summary>
    /// Checks that each range page has the length of the matching store page, and that the
    /// range slices start at <paramref name="offset"/> on the same page boundaries. A range made from another page layout of the same records
    /// fails this check, and the caller then makes a new range.
    /// </summary>
    internal bool MatchesPageLayout(UIRecordStore<T> store, int offset)
    {
        if (Length == 0 || offset < 0 || offset > store.Length - Length)
            return false;
        int firstPage = store.LocatePage(offset, out int headOffset);
        if (headOffset != _headOffset || firstPage + PageCount > store.PageCount)
            return false;
        for (int page = 0; page < PageCount; page++)
            if (Math.Min(store.GetPageStart(firstPage + page + 1) - offset, Length) != _ends[page] ||
                SourcePageLength(page) != store.GetPageLength(firstPage + page))
                return false;
        return true;
    }

    private int SourcePageLength(int page)
        => page == 0 ? _firstSourcePageLength :
            page == PageCount - 1 ? _lastSourcePageLength :
            _leafStore[page].Leaf.Values!.Length;

    /// <summary>Replaces one page with a page of the same record count.</summary>
    internal UIRecordRange<T> WithReplacedPage(int pageIndex, UIRecordStoreNode<T> leaf)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        ArgumentNullException.ThrowIfNull(leaf);
        if ((uint)pageIndex >= (uint)PageCount ||
            leaf.Values is not { } values ||
            values.Length != SourcePageLength(pageIndex))
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        int start = pageIndex == 0 ? _headOffset : 0;
        int count = _ends[pageIndex] - (pageIndex == 0 ? 0 : _ends[pageIndex - 1]);
        UIRecordStoreNode<T>? owned = null;
        UIRecordStore<UIRecordLeafClaim<T>>? leafStore = null;
        try
        {
            UIRecordStoreNode<T> replacement = leaf;
            if (_copyBoundaryRecords && (start != 0 || count != values.Length))
                replacement = owned = leaf.CopySlice(start, count);
            int slotPage = _leafStore.LocatePage(pageIndex, out int slot);
            UIRecordLeafClaim<T>[] slots = _leafStore.GetPage(slotPage).ToArray();
            slots[slot] = new UIRecordLeafClaim<T>(replacement);
            leafStore = _leafStore.WithReplacedPage(slotPage, slots);
            UIRecordStoreNode<T>? local = owned;
            owned = null;
            local?.Release();
            UIRecordRange<T> range = new(leafStore, _copyBoundaryRecords, _headOffset,
                _firstSourcePageLength, _lastSourcePageLength, _ends, Length);
            leafStore = null;
            return range;
        }
        catch (Exception failure)
        {
            List<Exception> cleanup = [];
            try { leafStore?.Release(); } catch (Exception error) { cleanup.Add(error); }
            try { owned?.Release(); } catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
            {
                cleanup.Insert(0, failure);
                throw new AggregateException("UI record page replacement and cleanup failed.", cleanup);
            }
            throw;
        }
    }

    internal T this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(ResourcesRetired, this);
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            int page = PageOf(index);
            int sliceStart = page == 0 ? 0 : _ends[page - 1];
            int leafOffset = !_copyBoundaryRecords && page == 0 ? _headOffset : 0;
            return _leafStore[page].Leaf.Values![leafOffset + index - sliceStart];
        }
    }

    internal ReadOnlySpan<T> GetPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if ((uint)pageIndex >= (uint)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        int sliceStart = pageIndex == 0 ? 0 : _ends[pageIndex - 1];
        int leafOffset = !_copyBoundaryRecords && pageIndex == 0 ? _headOffset : 0;
        return _leafStore[pageIndex].Leaf.Values!.AsSpan(leafOffset,
            _ends[pageIndex] - sliceStart);
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

    private int PageOf(int index)
    {
        int low = 0;
        int high = _ends.Length - 1;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (_ends[middle] > index)
                high = middle;
            else
                low = middle + 1;
        }
        return low;
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
