using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>
/// Owns immutable record pages and their optional resource claims. A store that
/// <see cref="Create"/> builds has full pages of <see cref="PageSize"/> records, except
/// the last page. A store that <see cref="Splice"/> builds can have shorter pages; it
/// keeps the first record index of each page so that index lookup stays exact.
/// </summary>
internal sealed class UIRecordStore<T> where T : struct
{
    internal const int PageSize = 256;
    private const int BranchWidth = 32;
    private const int MaximumRecords = 1 << 24;

    private readonly UIRecordStoreNode<T> _root;
    private readonly Action<T>? _retainRecord;
    private readonly Action<T>? _releaseRecord;
    private readonly int _depth;
    private readonly int _pageCount;
    // Null when every page except the last has PageSize records. Otherwise PageCount + 1 starts.
    private readonly int[]? _pageStarts;
    private int _claims = 1;
    private int _retired;

    private UIRecordStore(UIRecordStoreNode<T> root, int length, int depth, int pageCount,
        int[]? pageStarts, Action<T>? retainRecord, Action<T>? releaseRecord)
    {
        _root = root;
        Length = length;
        _depth = depth;
        _pageCount = pageCount;
        _pageStarts = pageStarts;
        _retainRecord = retainRecord;
        _releaseRecord = releaseRecord;
    }

    internal int Length { get; }
    internal int PageCount => _pageCount;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;
    /// <summary>Gets whether every page except the last has <see cref="PageSize"/> records.</summary>
    internal bool HasUniformPages => _pageStarts is null;

    /// <summary>Gets the store index of the first record in a page.</summary>
    internal int GetPageStart(int pageIndex)
    {
        if ((uint)pageIndex > (uint)_pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return _pageStarts?[pageIndex] ?? Math.Min(Length, pageIndex * PageSize);
    }

    /// <summary>Gets the record count of a page.</summary>
    internal int GetPageLength(int pageIndex)
        => GetPageStart(pageIndex + 1) - GetPageStart(pageIndex);

    /// <summary>Finds the page that holds a record and the record offset in that page.</summary>
    internal int LocatePage(int index, out int offset)
    {
        if ((uint)index >= (uint)Length)
            throw new IndexOutOfRangeException();
        if (_pageStarts is not { } starts)
        {
            offset = index % PageSize;
            return index / PageSize;
        }
        int low = 0;
        int high = _pageCount - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >>> 1;
            if (starts[middle] <= index)
                low = middle;
            else
                high = middle - 1;
        }
        offset = index - starts[low];
        return low;
    }

    /// <summary>
    /// Each retain callback must acquire one claim or throw before acquisition.
    /// Each release callback must consume its claim or quarantine it before throwing.
    /// </summary>
    internal static UIRecordStore<T> Create(ReadOnlySpan<T> records,
        Action<T>? retainRecord = null, Action<T>? releaseRecord = null)
    {
        if (records.Length > MaximumRecords)
            throw new ArgumentOutOfRangeException(nameof(records));
        if ((retainRecord is null) != (releaseRecord is null))
            throw new ArgumentException("Record retain and release actions must be paired.");
        int pages = Math.Max(1, (records.Length + PageSize - 1) / PageSize);
        int depth = DepthFor(pages, out long covered);
        UIRecordStoreNode<T> root = Build(records, 0, records.Length, depth, covered / BranchWidth,
            retainRecord, releaseRecord);
        return new UIRecordStore<T>(root, records.Length, depth,
            (records.Length + PageSize - 1) / PageSize, null, retainRecord, releaseRecord);
    }

    private static int DepthFor(int leaves, out long covered)
    {
        int depth = 0;
        covered = 1;
        while (covered < leaves)
        {
            covered = checked(covered * BranchWidth);
            depth++;
        }
        return depth;
    }

    private static UIRecordStoreNode<T> Build(ReadOnlySpan<T> values, int offset, int length,
        int depth, long childCoverage, Action<T>? retainRecord, Action<T>? releaseRecord)
    {
        if (depth == 0)
            return new UIRecordStoreNode<T>(values.Slice(offset, length), retainRecord, releaseRecord);
        UIRecordStoreNode<T>?[] children = new UIRecordStoreNode<T>[BranchWidth];
        try
        {
            long childRecords = childCoverage * PageSize;
            for (int index = 0; index < BranchWidth; index++)
            {
                long start = index * childRecords;
                if (start >= length)
                    break;
                int count = (int)Math.Min(childRecords, length - start);
                children[index] = Build(values, offset + (int)start, count,
                    depth - 1, childCoverage / BranchWidth,
                    retainRecord, releaseRecord);
            }
            UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordPathsCopied);
            return new UIRecordStoreNode<T>(children);
        }
        catch (Exception failure)
        {
            throw ReleasePartialChildren(children, failure);
        }
    }

    internal ReadOnlySpan<T> GetPage(int pageIndex)
        => GetLeaf(pageIndex).Values!;

    internal UIRecordStoreNode<T> GetLeaf(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        if ((uint)pageIndex >= (uint)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        UIRecordStoreNode<T> node = _root;
        int divisor = 1;
        for (int level = 0; level < _depth; level++)
            divisor = checked(divisor * BranchWidth);
        for (int level = _depth; level > 0; level--)
        {
            divisor /= BranchWidth;
            node = node.Children![(pageIndex / divisor) % BranchWidth]!;
        }
        return node;
    }

    internal ref readonly T this[int index]
    {
        get
        {
            int page = LocatePage(index, out int offset);
            return ref GetPage(page)[offset];
        }
    }

    internal UIRecordStore<T> WithReplacedPage(int pageIndex, ReadOnlySpan<T> records)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        if ((uint)pageIndex >= (uint)PageCount || records.Length != GetPageLength(pageIndex))
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        UIRecordStoreNode<T> root = Replace(_root, _depth, pageIndex, records,
            _retainRecord, _releaseRecord);
        return new UIRecordStore<T>(root, Length, _depth, _pageCount, _pageStarts,
            _retainRecord, _releaseRecord);
    }

    /// <summary>
    /// Replaces several pages in one copy-on-write update. Page indices must ascend,
    /// and each page must have the same record count as the page it replaces.
    /// The new store adopts the page arrays; callers must not change them afterward.
    /// Unchanged leaves and branches are shared with this store.
    /// </summary>
    internal UIRecordStore<T> WithReplacedPages(ReadOnlySpan<int> pageIndices, ReadOnlySpan<T[]> pages)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        if (pageIndices.Length != pages.Length)
            throw new ArgumentException("Each replaced page index needs one page.", nameof(pages));
        if (pageIndices.IsEmpty)
        {
            Retain();
            return this;
        }
        for (int index = 0; index < pageIndices.Length; index++)
        {
            int pageIndex = pageIndices[index];
            if ((uint)pageIndex >= (uint)PageCount ||
                index > 0 && pageIndex <= pageIndices[index - 1])
                throw new ArgumentOutOfRangeException(nameof(pageIndices));
            if (pages[index] is null || pages[index].Length != GetPageLength(pageIndex))
                throw new ArgumentException("A replacement page has the wrong length.", nameof(pages));
        }
        UIRecordStoreNode<T> root = ReplaceMany(_root, _depth, pageIndices, pages,
            _retainRecord, _releaseRecord);
        return new UIRecordStore<T>(root, Length, _depth, _pageCount, _pageStarts,
            _retainRecord, _releaseRecord);
    }

    /// <summary>Copies all records in order to caller-owned storage.</summary>
    internal void CopyTo(Span<T> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        if (destination.Length < Length)
            throw new ArgumentException("The destination is too short.", nameof(destination));
        for (int page = 0; page < PageCount; page++)
            GetPage(page).CopyTo(destination[GetPageStart(page)..]);
    }

    /// <summary>
    /// Applies ordered, disjoint edits in one copy-on-write update. Edits must ascend by
    /// <see cref="UIRecordSplice.OldStart"/>. The new store shares each page that no edit
    /// touches and copies only the pages that an edit touches, so the copied record count is
    /// at most the inserted records plus two pages for each edit. Branches are rebuilt over
    /// the page sequence. When short pages accumulate, the store packs all records again.
    /// </summary>
    internal UIRecordStore<T> Splice(ReadOnlySpan<UIRecordSplice> edits, ReadOnlySpan<T> inserted)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        long newLength = Length;
        int previousEnd = 0;
        for (int index = 0; index < edits.Length; index++)
        {
            UIRecordSplice edit = edits[index];
            if (edit.OldStart < previousEnd || edit.OldCount < 0 || edit.NewCount < 0 ||
                edit.OldStart > Length - edit.OldCount || edit.NewStart < 0 ||
                edit.NewStart > inserted.Length - edit.NewCount)
                throw new ArgumentOutOfRangeException(nameof(edits));
            previousEnd = edit.OldStart + edit.OldCount;
            newLength += edit.Delta;
        }
        if (newLength > MaximumRecords)
            throw new ArgumentOutOfRangeException(nameof(edits));
        if (edits.IsEmpty)
        {
            Retain();
            return this;
        }

        List<UIRecordStoreNode<T>> leaves = new(_pageCount + edits.Length * 2);
        List<int> starts = new(_pageCount + edits.Length * 2 + 1);
        List<T> group = [];
        int length = 0;
        try
        {
            int page = 0;
            int next = 0;
            while (next < edits.Length)
            {
                int first = TouchedPage(edits[next].OldStart);
                int last = LastTouchedPage(edits[next]);
                int end = next + 1;
                while (end < edits.Length && TouchedPage(edits[end].OldStart) <= last)
                    last = Math.Max(last, LastTouchedPage(edits[end++]));
                for (; page < first; page++)
                    AddSharedLeaf(page, leaves, starts, ref length);

                // Copy the touched pages with every edit in this group applied.
                group.Clear();
                int cursor = first < _pageCount ? GetPageStart(first) : Length;
                for (int index = next; index < end; index++)
                {
                    UIRecordSplice edit = edits[index];
                    AppendOld(cursor, edit.OldStart, group);
                    group.AddRange(inserted.Slice(edit.NewStart, edit.NewCount));
                    cursor = edit.OldStart + edit.OldCount;
                }
                AppendOld(cursor, last < _pageCount ? GetPageStart(last + 1) : Length, group);
                AddCopiedLeaves(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(group),
                    leaves, starts, ref length);
                page = Math.Max(page, last + 1);
                next = end;
            }
            for (; page < _pageCount; page++)
                AddSharedLeaf(page, leaves, starts, ref length);
            starts.Add(length);
            if (length != newLength)
                throw new InvalidOperationException("UI record splice produced the wrong record count.");
            return FromLeaves(leaves, starts);
        }
        catch (Exception failure)
        {
            List<Exception>? failures = null;
            foreach (UIRecordStoreNode<T> leaf in leaves)
                try { leaf.Release(); }
                catch (Exception cleanupFailure) { (failures ??= [failure]).Add(cleanupFailure); }
            if (failures is not null)
                throw new AggregateException("UI record splice and cleanup failed.", failures);
            throw;
        }
    }

    /// <summary>Gets the page that an edit at an old index starts in. An append uses the last page.</summary>
    private int TouchedPage(int oldStart)
    {
        if (_pageCount == 0)
            return 0;
        return oldStart >= Length ? _pageCount - 1 : LocatePage(oldStart, out _);
    }

    private int LastTouchedPage(UIRecordSplice edit)
        => edit.OldCount == 0 ? TouchedPage(edit.OldStart) :
            LocatePage(edit.OldStart + edit.OldCount - 1, out _);

    private void AppendOld(int start, int end, List<T> destination)
    {
        while (start < end)
        {
            int page = LocatePage(start, out int offset);
            ReadOnlySpan<T> values = GetPage(page);
            int count = Math.Min(values.Length - offset, end - start);
            destination.AddRange(values.Slice(offset, count));
            start += count;
        }
    }

    private void AddSharedLeaf(int page, List<UIRecordStoreNode<T>> leaves, List<int> starts,
        ref int length)
    {
        UIRecordStoreNode<T> leaf = GetLeaf(page);
        leaf.Retain();
        leaves.Add(leaf);
        starts.Add(length);
        length += leaf.Values!.Length;
    }

    /// <summary>Splits copied records into pages of nearly equal length.</summary>
    private void AddCopiedLeaves(ReadOnlySpan<T> records, List<UIRecordStoreNode<T>> leaves,
        List<int> starts, ref int length)
    {
        int pages = (records.Length + PageSize - 1) / PageSize;
        int offset = 0;
        for (int index = 0; index < pages; index++)
        {
            int count = (records.Length - offset) / (pages - index);
            leaves.Add(new UIRecordStoreNode<T>(records.Slice(offset, count),
                _retainRecord, _releaseRecord));
            starts.Add(length);
            offset += count;
            length += count;
        }
    }

    /// <summary>
    /// Builds new branches over the pages, or packs short pages again. The new store takes
    /// its own page claims; the caller keeps the claims in <paramref name="leaves"/>.
    /// </summary>
    private UIRecordStore<T> FromLeaves(List<UIRecordStoreNode<T>> leaves, List<int> starts)
    {
        int length = starts[^1];
        int pageCount = leaves.Count;
        int fullPages = (length + PageSize - 1) / PageSize;
        if (pageCount > fullPages * 2 + 4)
        {
            // Many short pages slow lookup. One packing pass restores full pages.
            T[] packed = new T[length];
            for (int index = 0; index < leaves.Count; index++)
                leaves[index].Values!.CopyTo(packed, starts[index]);
            UIRecordStore<T> compact = Create(packed, _retainRecord, _releaseRecord);
            ReleaseLeaves(leaves);
            return compact;
        }
        bool uniform = true;
        for (int index = 0; uniform && index < pageCount - 1; index++)
            uniform = leaves[index].Values!.Length == PageSize;
        if (pageCount == 0)
            leaves.Add(new UIRecordStoreNode<T>(ReadOnlySpan<T>.Empty, _retainRecord, _releaseRecord));
        int depth = DepthFor(leaves.Count, out long covered);
        UIRecordStoreNode<T> root = BuildBranches(leaves, 0, leaves.Count, depth, covered / BranchWidth);
        UIRecordStore<T> store = new(root, length, depth, pageCount,
            uniform ? null : starts.ToArray(), _retainRecord, _releaseRecord);
        try { ReleaseLeaves(leaves); }
        catch
        {
            store.Release();
            throw;
        }
        return store;
    }

    private static void ReleaseLeaves(List<UIRecordStoreNode<T>> leaves)
    {
        List<Exception>? failures = null;
        foreach (UIRecordStoreNode<T> leaf in leaves)
            try { leaf.Release(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        leaves.Clear();
        if (failures is not null)
            throw new AggregateException("UI record page cleanup failed.", failures);
    }

    /// <summary>Builds branches that take one new claim on each page.</summary>
    private static UIRecordStoreNode<T> BuildBranches(List<UIRecordStoreNode<T>> leaves,
        int offset, int count, int depth, long childCoverage)
    {
        if (depth == 0)
        {
            UIRecordStoreNode<T> leaf = leaves[offset];
            leaf.Retain();
            return leaf;
        }
        UIRecordStoreNode<T>?[] children = new UIRecordStoreNode<T>[BranchWidth];
        try
        {
            for (int index = 0; index < BranchWidth; index++)
            {
                long start = index * childCoverage;
                if (start >= count)
                    break;
                int childCount = (int)Math.Min(childCoverage, count - start);
                children[index] = BuildBranches(leaves, offset + (int)start, childCount,
                    depth - 1, childCoverage / BranchWidth);
            }
        }
        catch (Exception failure)
        {
            throw ReleasePartialChildren(children, failure);
        }
        UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordPathsCopied);
        return new UIRecordStoreNode<T>(children);
    }

    private static UIRecordStoreNode<T> ReplaceMany(UIRecordStoreNode<T> source, int depth,
        ReadOnlySpan<int> pageIndices, ReadOnlySpan<T[]> pages,
        Action<T>? retainRecord, Action<T>? releaseRecord)
    {
        if (depth == 0)
            return new UIRecordStoreNode<T>(pages[0], retainRecord, releaseRecord);
        int divisor = 1;
        for (int level = 1; level < depth; level++)
            divisor = checked(divisor * BranchWidth);
        UIRecordStoreNode<T>?[] children = new UIRecordStoreNode<T>[BranchWidth];
        try
        {
            int next = 0;
            for (int index = 0; index < BranchWidth; index++)
            {
                UIRecordStoreNode<T>? child = source.Children![index];
                if (child is null)
                    continue;
                int end = next;
                while (end < pageIndices.Length && pageIndices[end] / divisor % BranchWidth == index)
                    end++;
                if (end > next)
                    children[index] = ReplaceMany(child, depth - 1, pageIndices[next..end],
                        pages[next..end], retainRecord, releaseRecord);
                else
                {
                    child.Retain();
                    children[index] = child;
                }
                next = end;
            }
            UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordPathsCopied);
            return new UIRecordStoreNode<T>(children);
        }
        catch (Exception failure)
        {
            throw ReleasePartialChildren(children, failure);
        }
    }

    private static UIRecordStoreNode<T> Replace(UIRecordStoreNode<T> source, int depth, int pageIndex,
        ReadOnlySpan<T> records, Action<T>? retainRecord, Action<T>? releaseRecord)
    {
        if (depth == 0)
            return new UIRecordStoreNode<T>(records, retainRecord, releaseRecord);
        int divisor = 1;
        for (int level = 1; level < depth; level++)
            divisor = checked(divisor * BranchWidth);
        int changed = (pageIndex / divisor) % BranchWidth;
        UIRecordStoreNode<T>?[] children = new UIRecordStoreNode<T>[BranchWidth];
        try
        {
            for (int index = 0; index < BranchWidth; index++)
            {
                UIRecordStoreNode<T>? child = source.Children![index];
                if (child is null)
                    continue;
                if (index == changed)
                    children[index] = Replace(child, depth - 1, pageIndex,
                        records, retainRecord, releaseRecord);
                else
                {
                    child.Retain();
                    children[index] = child;
                }
            }
            UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordPathsCopied);
            return new UIRecordStoreNode<T>(children);
        }
        catch (Exception failure)
        {
            throw ReleasePartialChildren(children, failure);
        }
    }

    private static Exception ReleasePartialChildren(UIRecordStoreNode<T>?[] children,
        Exception failure)
    {
        List<Exception>? failures = null;
        foreach (UIRecordStoreNode<T>? child in children)
            try { child?.Release(); }
            catch (Exception cleanupFailure)
            {
                (failures ??= [failure]).Add(cleanupFailure);
            }
        return failures is null ? failure :
            new AggregateException("UI record store construction and cleanup failed.", failures);
    }

    internal void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _claims, checked(current + 1), current) != current);
    }

    internal void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            if (current == 0)
                throw new InvalidOperationException("A UI record store was released twice.");
        } while (Interlocked.CompareExchange(ref _claims, current - 1, current) != current);
        if (current != 1)
            return;
        try { RetireRoot(); }
        finally { GC.SuppressFinalize(this); }
    }

    ~UIRecordStore() => RetireAbandoned();

    private void RetireAbandoned()
    {
        Interlocked.Exchange(ref _claims, 0);
        try { RetireRoot(); }
        catch (Exception failure)
        {
            try
            {
                System.Diagnostics.Trace.TraceError("UI record store final cleanup failed: {0}", failure);
            }
            catch { }
        }
    }

    private void RetireRoot()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0)
            _root.Release();
    }
}
