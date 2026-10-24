using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>
/// Owns the exact record pages that intersect one ordered range. The range does not depend
/// on its position in the source store, so a copy can follow the same records after an
/// edit moves them.
/// </summary>
internal sealed class UIRecordRange<T> where T : struct
{
    private static readonly Action<UIRecordLeafClaim<T>> RetainLeaf =
        static claim => claim.Leaf.Retain();
    private static readonly Action<UIRecordLeafClaim<T>> ReleaseLeaf =
        static claim => claim.Leaf.Release();
    private readonly UIRecordStore<UIRecordLeafClaim<T>> _leafStore;
    // Offset of the first range record in the first page.
    private readonly int _headOffset;
    // Range-relative end of each page slice.
    private readonly int[] _ends;
    private int _retired;

    private UIRecordRange(UIRecordStore<UIRecordLeafClaim<T>> leafStore,
        int headOffset, int[] ends, int length)
    {
        _leafStore = leafStore;
        _headOffset = headOffset;
        _ends = ends;
        Length = length;
    }

    internal int Length { get; }
    internal int PageCount => _ends.Length;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;

    internal static UIRecordRange<T> Create(UIRecordStore<T> store, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (offset < 0 || length < 0 || offset > store.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length == 0)
            return new UIRecordRange<T>(UIRecordStore<UIRecordLeafClaim<T>>.Create([],
                RetainLeaf, ReleaseLeaf), 0, [], 0);
        int firstPage = store.LocatePage(offset, out int headOffset);
        int lastPage = store.LocatePage(offset + length - 1, out _);
        UIRecordLeafClaim<T>[] leaves = new UIRecordLeafClaim<T>[lastPage - firstPage + 1];
        int[] ends = new int[leaves.Length];
        int covered = 0;
        for (int index = 0; index < leaves.Length; index++)
        {
            UIRecordStoreNode<T> leaf = store.GetLeaf(firstPage + index);
            leaves[index] = new UIRecordLeafClaim<T>(leaf);
            int start = index == 0 ? headOffset : 0;
            covered += Math.Min(leaf.Values!.Length - start, length - covered);
            ends[index] = covered;
        }
        UIRecordStore<UIRecordLeafClaim<T>> leafStore =
            UIRecordStore<UIRecordLeafClaim<T>>.Create(leaves, RetainLeaf, ReleaseLeaf);
        return new UIRecordRange<T>(leafStore, headOffset, ends, length);
    }

    /// <summary>Takes a second owner of the same pages. The caller releases each owner once.</summary>
    internal UIRecordRange<T> Share()
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        _leafStore.Retain();
        return new UIRecordRange<T>(_leafStore, _headOffset, _ends, Length);
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
                _leafStore[page].Leaf.Values!.Length != store.GetPageLength(firstPage + page))
                return false;
        return true;
    }

    /// <summary>Replaces one page with a page of the same record count.</summary>
    internal UIRecordRange<T> WithReplacedPage(int pageIndex, UIRecordStoreNode<T> leaf)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        ArgumentNullException.ThrowIfNull(leaf);
        if ((uint)pageIndex >= (uint)PageCount ||
            leaf.Values is not { } values ||
            values.Length != _leafStore[pageIndex].Leaf.Values!.Length)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        int slotPage = _leafStore.LocatePage(pageIndex, out int slot);
        UIRecordLeafClaim<T>[] slots = _leafStore.GetPage(slotPage).ToArray();
        slots[slot] = new UIRecordLeafClaim<T>(leaf);
        UIRecordStore<UIRecordLeafClaim<T>> leafStore =
            _leafStore.WithReplacedPage(slotPage, slots);
        return new UIRecordRange<T>(leafStore, _headOffset, _ends, Length);
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
            int leafOffset = page == 0 ? _headOffset : 0;
            return _leafStore[page].Leaf.Values![leafOffset + index - sliceStart];
        }
    }

    internal ReadOnlySpan<T> GetPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if ((uint)pageIndex >= (uint)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        int sliceStart = pageIndex == 0 ? 0 : _ends[pageIndex - 1];
        int leafOffset = pageIndex == 0 ? _headOffset : 0;
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
