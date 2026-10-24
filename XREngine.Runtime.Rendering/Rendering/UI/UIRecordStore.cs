using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Owns immutable record pages and their optional resource claims.</summary>
internal sealed class UIRecordStore<T> where T : struct
{
    internal const int PageSize = 256;
    private const int BranchWidth = 32;
    private const int MaximumRecords = 1 << 24;

    private readonly UIRecordStoreNode<T> _root;
    private readonly Action<T>? _retainRecord;
    private readonly Action<T>? _releaseRecord;
    private readonly int _depth;
    private int _claims = 1;
    private int _retired;

    private UIRecordStore(UIRecordStoreNode<T> root, int length, int depth,
        Action<T>? retainRecord, Action<T>? releaseRecord)
    {
        _root = root;
        Length = length;
        _depth = depth;
        _retainRecord = retainRecord;
        _releaseRecord = releaseRecord;
    }

    internal int Length { get; }
    internal int PageCount => (Length + PageSize - 1) / PageSize;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;

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
        int depth = 0;
        long covered = 1;
        while (covered < pages)
        {
            covered = checked(covered * BranchWidth);
            depth++;
        }
        UIRecordStoreNode<T> root = Build(records, 0, records.Length, depth, covered / BranchWidth,
            retainRecord, releaseRecord);
        return new UIRecordStore<T>(root, records.Length, depth, retainRecord, releaseRecord);
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
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();
            return ref GetPage(index / PageSize)[index % PageSize];
        }
    }

    internal UIRecordStore<T> WithReplacedPage(int pageIndex, ReadOnlySpan<T> records)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        int expected = Math.Min(PageSize, Length - pageIndex * PageSize);
        if ((uint)pageIndex >= (uint)PageCount || records.Length != expected)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        UIRecordStoreNode<T> root = Replace(_root, _depth, pageIndex, records,
            _retainRecord, _releaseRecord);
        return new UIRecordStore<T>(root, Length, _depth, _retainRecord, _releaseRecord);
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
            if (pages[index] is null ||
                pages[index].Length != Math.Min(PageSize, Length - pageIndex * PageSize))
                throw new ArgumentException("A replacement page has the wrong length.", nameof(pages));
        }
        UIRecordStoreNode<T> root = ReplaceMany(_root, _depth, pageIndices, pages,
            _retainRecord, _releaseRecord);
        return new UIRecordStore<T>(root, Length, _depth, _retainRecord, _releaseRecord);
    }

    /// <summary>Copies all records in order to caller-owned storage.</summary>
    internal void CopyTo(Span<T> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        if (destination.Length < Length)
            throw new ArgumentException("The destination is too short.", nameof(destination));
        for (int page = 0; page < PageCount; page++)
            GetPage(page).CopyTo(destination[(page * PageSize)..]);
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
