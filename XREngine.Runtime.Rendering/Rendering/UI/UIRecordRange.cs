using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Owns the exact record leaves that intersect one ordered range.</summary>
internal sealed class UIRecordRange<T> where T : struct
{
    private readonly UIRecordStoreNode<T>[] _leaves;
    private readonly int _firstPage;
    private readonly int _offset;
    private int _retired;

    private UIRecordRange(UIRecordStoreNode<T>[] leaves, int firstPage, int offset, int length)
    {
        _leaves = leaves;
        _firstPage = firstPage;
        _offset = offset;
        Length = length;
    }

    internal int Length { get; }
    internal int PageCount => _leaves.Length;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;

    internal static UIRecordRange<T> Create(UIRecordStore<T> store, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (offset < 0 || length < 0 || offset > store.Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int firstPage = offset / UIRecordStore<T>.PageSize;
        int endPage = length == 0 ? firstPage :
            (offset + length - 1) / UIRecordStore<T>.PageSize + 1;
        UIRecordStoreNode<T>[] leaves = new UIRecordStoreNode<T>[endPage - firstPage];
        int retained = 0;
        try
        {
            for (; retained < leaves.Length; retained++)
            {
                UIRecordStoreNode<T> leaf = store.GetLeaf(firstPage + retained);
                leaf.Retain();
                leaves[retained] = leaf;
            }
            return new UIRecordRange<T>(leaves, firstPage, offset, length);
        }
        catch (Exception failure)
        {
            List<Exception>? failures = null;
            for (int index = 0; index < retained; index++)
                try { leaves[index].Release(); }
                catch (Exception cleanupFailure) { (failures ??= [failure]).Add(cleanupFailure); }
            if (failures is not null)
                throw new AggregateException("UI record range construction and cleanup failed.", failures);
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
            int absolute = _offset + index;
            return _leaves[absolute / UIRecordStore<T>.PageSize - _firstPage]
                .Values![absolute % UIRecordStore<T>.PageSize];
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
        return _leaves[pageIndex].Values!.AsSpan(
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
    {
        List<Exception>? failures = null;
        foreach (UIRecordStoreNode<T> leaf in _leaves)
            try { leaf.Release(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        if (failures is not null)
            throw new AggregateException("UI record range cleanup failed.", failures);
    }
}
