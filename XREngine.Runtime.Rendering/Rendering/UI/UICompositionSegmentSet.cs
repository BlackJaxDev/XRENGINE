using System.Threading;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Owns immutable composition spans and their membership lookup.</summary>
internal sealed class UICompositionSegmentSet
{
    private static long _nextIdentity;
    private static long _nextRevision;
    private static readonly Action<UICompositionSegmentSlot> RetainSlot =
        static slot => slot.Segment.Retain();
    private static readonly Action<UICompositionSegmentSlot> ReleaseSlot =
        static slot => slot.Segment.Release();
    private readonly UIRecordStore<UICompositionSegmentSlot>? _segmentStore;
    private UICompositionSegment[]? _flatSegments;
    private readonly Dictionary<long, int> _byMembership;
    private readonly bool _immortal;
    private int _claims = 1;
    private int _retired;

    internal static UICompositionSegmentSet Empty { get; } = new([], [], true);

    private UICompositionSegmentSet(UICompositionSegment[] segments,
        Dictionary<long, int> byMembership, bool immortal = false)
    {
        _byMembership = byMembership;
        _immortal = immortal;
        if (immortal)
            return;
        UICompositionSegmentSlot[] slots = new UICompositionSegmentSlot[segments.Length];
        for (int index = 0; index < slots.Length; index++)
            slots[index] = new UICompositionSegmentSlot(segments[index]);
        _segmentStore = UIRecordStore<UICompositionSegmentSlot>.Create(slots,
            RetainSlot, ReleaseSlot);
    }

    private static UICompositionSegmentSet TakeOwnedSegments(
        UICompositionSegment[] segments, Dictionary<long, int> byMembership)
    {
        UICompositionSegmentSet set;
        try { set = new UICompositionSegmentSet(segments, byMembership); }
        catch (Exception failure)
        {
            try { ReleaseSegments(segments); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("UI composition set build and cleanup failed.",
                    failure, cleanupFailure);
            }
            throw;
        }
        try
        {
            ReleaseSegments(segments);
            return set;
        }
        catch (Exception failure)
        {
            try { set.Release(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("UI composition set transfer and cleanup failed.",
                    failure, cleanupFailure);
            }
            throw;
        }
    }

    private UICompositionSegmentSet(UIRecordStore<UICompositionSegmentSlot> segmentStore,
        Dictionary<long, int> byMembership)
    {
        _segmentStore = segmentStore;
        _byMembership = byMembership;
    }

    internal bool ResourcesRetired => !_immortal && Volatile.Read(ref _retired) != 0;
    internal ReadOnlySpan<UICompositionSegment> Segments
    {
        get
        {
            ObjectDisposedException.ThrowIf(ResourcesRetired, this);
            if (_flatSegments is { } flat)
                return flat;
            UIRecordStore<UICompositionSegmentSlot>? store = _segmentStore;
            if (store is null)
                return [];
            flat = new UICompositionSegment[store.Length];
            for (int index = 0; index < flat.Length; index++)
                flat[index] = store[index].Segment;
            UIRuntimeCounters.Add(UIWorkCounter.PublicationSegmentSlotsMaterialized, flat.Length);
            return Interlocked.CompareExchange(ref _flatSegments, flat, null) ?? flat;
        }
    }

    internal int Count => _segmentStore?.Length ?? 0;
    internal UICompositionSegment GetSegment(int index)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        return _segmentStore![index].Segment;
    }

    internal bool TryGet(long membershipId, out UICompositionSegment segment)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if (_byMembership.TryGetValue(membershipId, out int index))
        {
            segment = GetSegment(index);
            return true;
        }
        segment = null!;
        return false;
    }

    private static ulong NextRevision() => unchecked((ulong)Interlocked.Increment(ref _nextRevision));

    internal void Retain()
    {
        if (_immortal)
            return;
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _claims, checked(current + 1), current) != current);
    }

    internal void Release()
    {
        if (_immortal)
            return;
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            if (current == 0)
                throw new InvalidOperationException("A UI composition set was released twice.");
        } while (Interlocked.CompareExchange(ref _claims, current - 1, current) != current);
        if (current != 1)
            return;
        try { RetireSegments(); }
        finally { GC.SuppressFinalize(this); }
    }

    ~UICompositionSegmentSet()
    {
        if (_immortal)
            return;
        Interlocked.Exchange(ref _claims, 0);
        try { RetireSegments(); }
        catch (Exception failure)
        {
            try { System.Diagnostics.Trace.TraceError("UI composition set final cleanup failed: {0}", failure); }
            catch { }
        }
    }

    private void RetireSegments()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;
        _segmentStore?.Release();
    }

    private static void ReleaseSegments(ReadOnlySpan<UICompositionSegment> segments)
    {
        List<Exception>? failures = null;
        foreach (UICompositionSegment segment in segments)
            try { segment.Release(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        if (failures is not null)
            throw new AggregateException("UI composition segment cleanup failed.", failures);
    }

    /// <summary>
    /// Updates only the segments that intersect changed record pages. Returns false when
    /// a changed page alters membership or composition ownership; the caller then rebuilds.
    /// </summary>
    internal bool TryWithReplacedRenderPages(UIRecordStore<UIRenderRecord> records,
        ReadOnlySpan<int> changedPages, out UICompositionSegmentSet updated)
    {
        updated = null!;
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if (_immortal)
        {
            // An empty set stays valid only while no changed record names an explicit owner.
            for (int pageIndex = 0; pageIndex < changedPages.Length; pageIndex++)
                foreach (ref readonly UIRenderRecord record in records.GetPage(changedPages[pageIndex]))
                    if (record.CompositionOwner.IsExplicit)
                        return false;
            updated = this;
            return true;
        }
        if (records.Length != _byMembership.Count)
            return false;

        UICompositionSegmentSet? current = this;
        Retain();
        try
        {
            for (int pageIndex = 0; pageIndex < changedPages.Length; pageIndex++)
            {
                int page = changedPages[pageIndex];
                int pageStart = page * UIRecordStore<UIRenderRecord>.PageSize;
                int pageEnd = Math.Min(records.Length, pageStart + UIRecordStore<UIRenderRecord>.PageSize);
                if (pageStart >= pageEnd ||
                    !_byMembership.TryGetValue(records[pageStart].MembershipId, out int first) ||
                    !_byMembership.TryGetValue(records[pageEnd - 1].MembershipId, out int last) ||
                    first > last)
                    return false;
                for (int segmentIndex = first; segmentIndex <= last; segmentIndex++)
                {
                    UICompositionSegment segment = current.GetSegment(segmentIndex);
                    // Keep the segment revision when its records in this page are equal.
                    int overlapStart = Math.Max(segment.Start, pageStart);
                    int overlapEnd = Math.Min(segment.Start + segment.Count, pageEnd);
                    bool equal = true;
                    for (int index = overlapStart; equal && index < overlapEnd; index++)
                        equal = segment.GetRecord(index - segment.Start).Equals(records[index]);
                    if (equal)
                        continue;
                    if (!current.TryWithReplacedRenderPage(segmentIndex, segment.Start, page,
                            records, out UICompositionSegmentSet next))
                        return false;
                    UICompositionSegmentSet replaced = current;
                    current = next;
                    replaced.Release();
                }
            }
            updated = current;
            current = null;
            return true;
        }
        finally { current?.Release(); }
    }

    internal bool TryWithReplacedRenderPage(int segmentIndex, int segmentStart,
        int recordPageIndex, UIRecordStore<UIRenderRecord> records,
        out UICompositionSegmentSet updated)
    {
        updated = null!;
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if ((uint)segmentIndex >= (uint)Count ||
            records.Length != _byMembership.Count ||
            (uint)recordPageIndex >= (uint)records.PageCount)
            return false;
        UICompositionSegment prior = GetSegment(segmentIndex);
        if (segmentStart != prior.Start)
            return false;
        long pageStart = (long)recordPageIndex * UIRecordStore<UIRenderRecord>.PageSize;
        long segmentEnd = (long)segmentStart + prior.Count;
        int first = (int)Math.Max(segmentStart, pageStart);
        int end = (int)Math.Min(segmentEnd,
            pageStart + UIRecordStore<UIRenderRecord>.PageSize);
        if (first >= end || segmentStart < 0 || segmentEnd > records.Length)
            return false;
        for (int index = first; index < end; index++)
        {
            UIRenderRecord oldRecord = prior.GetRecord(index - segmentStart);
            ref readonly UIRenderRecord record = ref records[index];
            if (record.MembershipId != oldRecord.MembershipId ||
                record.CompositionOwner != prior.Owner)
                return false;
        }

        UICompositionSegment replacement = prior.WithReplacedPage(NextRevision(),
            recordPageIndex - segmentStart / UIRecordStore<UIRenderRecord>.PageSize,
            records);
        try
        {
            int slotPage = segmentIndex / UIRecordStore<UICompositionSegmentSlot>.PageSize;
            UICompositionSegmentSlot[] slots = _segmentStore!.GetPage(slotPage).ToArray();
            slots[segmentIndex % UIRecordStore<UICompositionSegmentSlot>.PageSize] =
                new UICompositionSegmentSlot(replacement);
            UIRuntimeCounters.Add(UIWorkCounter.PublicationSegmentSlotsCopied, slots.Length);
            UIRecordStore<UICompositionSegmentSlot> store =
                _segmentStore.WithReplacedPage(slotPage, slots);
            updated = new UICompositionSegmentSet(store, _byMembership);
            return true;
        }
        finally { replacement.Release(); }
    }

    private bool TryUpdateSameTopology(UIRecordStore<UIRenderRecord> records,
        out UICompositionSegmentSet updated)
    {
        updated = null!;
        if (Count == 0 || _byMembership.Count != records.Length)
            return false;
        int offset = 0;
        for (int segmentIndex = 0; segmentIndex < Count; segmentIndex++)
        {
            UICompositionSegment segment = GetSegment(segmentIndex);
            for (int index = 0; index < segment.Count; index++)
            {
                ref readonly UIRenderRecord record = ref records[offset + index];
                if (record.MembershipId != segment.GetRecord(index).MembershipId ||
                    record.CompositionOwner != segment.Owner)
                    return false;
            }
            offset += segment.Count;
        }

        bool anyChanged = false;
        offset = 0;
        for (int segmentIndex = 0; segmentIndex < Count; segmentIndex++)
        {
            UICompositionSegment prior = GetSegment(segmentIndex);
            anyChanged |= !prior.ContentEquals(records, offset, prior.Count);
            offset += prior.Count;
        }
        if (!anyChanged)
        {
            Retain();
            updated = this;
            return true;
        }

        UICompositionSegment[] changed = new UICompositionSegment[Count];
        int retained = 0;
        offset = 0;
        bool transferred = false;
        try
        {
            for (; retained < Count; retained++)
            {
                UICompositionSegment prior = GetSegment(retained);
                if (prior.ContentEquals(records, offset, prior.Count))
                {
                    prior.Retain();
                    changed[retained] = prior;
                }
                else
                    changed[retained] = new UICompositionSegment(prior.Identity,
                        NextRevision(), prior.Owner, records, offset, prior.Count);
                offset += prior.Count;
            }
            transferred = true;
            updated = TakeOwnedSegments(changed, _byMembership);
            return true;
        }
        catch (Exception failure)
        {
            if (transferred)
                throw;
            try { ReleaseSegments(changed.AsSpan(0, retained)); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("UI composition update and cleanup failed.",
                    failure, cleanupFailure);
            }
            throw;
        }
    }

    /// <summary>Builds resource-free records for tests. Production passes its claimed store.</summary>
    internal static UICompositionSegmentSet Build(ReadOnlySpan<UIRenderRecord> records,
        UICompositionSegmentSet? previous)
    {
        UIRecordStore<UIRenderRecord> store = UIRecordStore<UIRenderRecord>.Create(records);
        try { return Build(store, previous); }
        finally { store.Release(); }
    }

    internal static UICompositionSegmentSet Build(UIRecordStore<UIRenderRecord> records,
        UICompositionSegmentSet? previous)
    {
        if (previous is not null && previous.TryUpdateSameTopology(records, out var updated))
            return updated;

        bool hasExplicitOwner = false;
        for (int index = 0; index < records.Length; index++)
            if (records[index].CompositionOwner.IsExplicit)
            {
                hasExplicitOwner = true;
                break;
            }
        if (!hasExplicitOwner)
            return Empty;

        List<UICompositionSegment> segments = [];
        Dictionary<long, int> byMembership = new(records.Length);
        HashSet<ulong> reused = [];
        bool transferred = false;
        try
        {
            int start = 0;
            while (start < records.Length)
            {
                UICompositionOwner owner = records[start].CompositionOwner;
                int end = start + 1;
                while (end < records.Length && records[end].CompositionOwner == owner)
                    end++;
                UICompositionSegment? prior = null;
                if (previous is not null)
                    for (int index = start; index < end; index++)
                        if (previous._byMembership.TryGetValue(records[index].MembershipId, out int candidateIndex) &&
                            previous.GetSegment(candidateIndex) is { } candidate &&
                            candidate.Owner == owner && reused.Add(candidate.Identity))
                        {
                            prior = candidate;
                            break;
                        }
                UICompositionSegment segment;
                if (prior is not null && prior.Start == start &&
                    prior.ContentEquals(records, start, end - start))
                {
                    prior.Retain();
                    segment = prior;
                }
                else
                {
                    ulong identity = prior?.Identity ?? unchecked((ulong)Interlocked.Increment(ref _nextIdentity));
                    segment = new UICompositionSegment(identity, NextRevision(), owner,
                        records, start, end - start);
                }
                try { segments.Add(segment); }
                catch
                {
                    segment.Release();
                    throw;
                }
                for (int index = start; index < end; index++)
                    byMembership.Add(records[index].MembershipId, segments.Count - 1);
                start = end;
            }
            UICompositionSegment[] staged = segments.ToArray();
            transferred = true;
            return TakeOwnedSegments(staged, byMembership);
        }
        catch (Exception failure)
        {
            if (transferred)
                throw;
            try { ReleaseSegments(CollectionsMarshal.AsSpan(segments)); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("UI composition build and cleanup failed.",
                    failure, cleanupFailure);
            }
            throw;
        }
    }
}
