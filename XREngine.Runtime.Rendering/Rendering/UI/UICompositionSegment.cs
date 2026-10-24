using System.Threading;

namespace XREngine.Rendering.UI;

/// <summary>Owns the record leaves for one immutable painter-order span.</summary>
public sealed class UICompositionSegment
{
    private readonly UIRecordRange<UIRenderRecord> _range;
    private UIRenderRecord[]? _flatRecords;
    private int _claims = 1;
    private int _retired;

    internal UICompositionSegment(ulong identity, ulong revision, UICompositionOwner owner,
        UIRecordStore<UIRenderRecord> store, int offset, int count)
    {
        _range = UIRecordRange<UIRenderRecord>.Create(store, offset, count);
        Identity = identity;
        Revision = revision;
        Owner = owner;
    }

    public ulong Identity { get; }
    public ulong Revision { get; }
    public UICompositionOwner Owner { get; }
    internal int Count => _range.Length;
    internal int PageCount => _range.PageCount;
    internal bool ResourcesRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>Materializes a contiguous compatibility view while this segment is retained.</summary>
    public ReadOnlySpan<UIRenderRecord> Records
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
            if (_flatRecords is { } records)
                return records;
            records = new UIRenderRecord[Count];
            _range.CopyTo(records);
            return Interlocked.CompareExchange(ref _flatRecords, records, null) ?? records;
        }
    }

    internal UIRenderRecord GetRecord(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        return _range[index];
    }

    internal ReadOnlySpan<UIRenderRecord> GetPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _claims) == 0, this);
        return _range.GetPage(pageIndex);
    }

    internal bool ContentEquals(UIRecordStore<UIRenderRecord> store, int offset, int count)
    {
        if (count != Count)
            return false;
        for (int index = 0; index < count; index++)
            if (!_range[index].Equals(store[offset + index]))
                return false;
        return true;
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
                throw new InvalidOperationException("A UI composition segment was released twice.");
        } while (Interlocked.CompareExchange(ref _claims, current - 1, current) != current);
        if (current != 1)
            return;
        try { RetireRange(); }
        finally { GC.SuppressFinalize(this); }
    }

    ~UICompositionSegment()
    {
        Interlocked.Exchange(ref _claims, 0);
        try { RetireRange(); }
        catch (Exception failure)
        {
            try { System.Diagnostics.Trace.TraceError("UI composition segment final cleanup failed: {0}", failure); }
            catch { }
        }
    }

    private void RetireRange()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0)
            _range.Release();
    }
}
