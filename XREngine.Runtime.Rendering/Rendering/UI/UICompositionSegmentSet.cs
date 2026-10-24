using System.Threading;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Owns immutable composition spans in painter order.</summary>
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
    // The record count of the store that these segments cover.
    private readonly int _recordCount;
    private readonly bool _immortal;
    private int _claims = 1;
    private int _retired;

    internal static UICompositionSegmentSet Empty { get; } = new([], 0, true);

    private UICompositionSegmentSet(UICompositionSegment[] segments,
        int recordCount, bool immortal = false)
    {
        _recordCount = recordCount;
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
        UICompositionSegment[] segments, int recordCount)
    {
        UICompositionSegmentSet set;
        try { set = new UICompositionSegmentSet(segments, recordCount); }
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
        int recordCount)
    {
        _segmentStore = segmentStore;
        _recordCount = recordCount;
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

    /// <summary>Finds the segment that holds a record index of the covered store.</summary>
    internal bool TryGetAt(int recordIndex, out UICompositionSegment segment)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        int index = FindSegment(recordIndex);
        segment = index >= 0 ? GetSegment(index) : null!;
        return index >= 0;
    }

    /// <summary>
    /// Advances a caller cursor to the segment that holds a record index. Indices must not
    /// decrease between calls with the same cursor, so a full traversal costs one step per segment.
    /// </summary>
    internal UICompositionSegment? Advance(ref int cursor, int recordIndex)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        while (cursor < Count)
        {
            UICompositionSegment segment = GetSegment(cursor);
            if (recordIndex < segment.Start)
                return null;
            if (recordIndex < segment.Start + segment.Count)
                return segment;
            cursor++;
        }
        return null;
    }

    private int FindSegment(int recordIndex)
    {
        if ((uint)recordIndex >= (uint)_recordCount)
            return -1;
        int low = 0;
        int high = Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            UICompositionSegment segment = GetSegment(middle);
            if (recordIndex < segment.Start)
                high = middle - 1;
            else if (recordIndex >= segment.Start + segment.Count)
                low = middle + 1;
            else
                return middle;
        }
        return -1;
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
        if (records.Length != _recordCount)
            return false;

        UICompositionSegmentSet? current = this;
        Retain();
        try
        {
            for (int pageIndex = 0; pageIndex < changedPages.Length; pageIndex++)
            {
                int page = changedPages[pageIndex];
                int pageStart = records.GetPageStart(page);
                int pageEnd = records.GetPageStart(page + 1);
                int first = FindSegment(pageStart);
                int last = FindSegment(pageEnd - 1);
                if (pageStart >= pageEnd || first < 0 || last < first)
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
            records.Length != _recordCount ||
            (uint)recordPageIndex >= (uint)records.PageCount)
            return false;
        UICompositionSegment prior = GetSegment(segmentIndex);
        if (segmentStart != prior.Start)
            return false;
        int pageStart = records.GetPageStart(recordPageIndex);
        long segmentEnd = (long)segmentStart + prior.Count;
        int first = (int)Math.Max(segmentStart, pageStart);
        int end = (int)Math.Min(segmentEnd, records.GetPageStart(recordPageIndex + 1));
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

        // A segment kept across a splice can hold pages of an older layout. It then takes
        // the current store pages, which costs one claim for each page in the segment.
        UICompositionSegment replacement = prior.MatchesPageLayout(records)
            ? prior.WithReplacedPage(NextRevision(),
                recordPageIndex - records.LocatePage(segmentStart, out _),
                records.GetLeaf(recordPageIndex))
            : new UICompositionSegment(prior.Identity, NextRevision(), prior.Owner,
                records, segmentStart, prior.Count);
        try
        {
            int slotPage = _segmentStore!.LocatePage(segmentIndex, out int slot);
            UICompositionSegmentSlot[] slots = _segmentStore.GetPage(slotPage).ToArray();
            slots[slot] = new UICompositionSegmentSlot(replacement);
            UIRuntimeCounters.Add(UIWorkCounter.PublicationSegmentSlotsCopied, slots.Length);
            UIRecordStore<UICompositionSegmentSlot> store =
                _segmentStore.WithReplacedPage(slotPage, slots);
            updated = new UICompositionSegmentSet(store, _recordCount);
            return true;
        }
        finally { replacement.Release(); }
    }

    private bool TryUpdateSameTopology(UIRecordStore<UIRenderRecord> records,
        out UICompositionSegmentSet updated)
    {
        updated = null!;
        if (Count == 0 || _recordCount != records.Length)
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
            updated = TakeOwnedSegments(changed, _recordCount);
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

    /// <summary>
    /// Updates the segments after <see cref="UIRecordStore{T}.Splice"/> applied
    /// <paramref name="edits"/> to the covered store. A segment that no edit touches keeps
    /// its object, or moves with a new revision when the edits shift it. Only runs that
    /// contain inserted records or lost records get new ranges, so the work depends on
    /// the segment count and the inserted records, not on the record count.
    /// </summary>
    internal UICompositionSegmentSet WithSplicedRenders(UIRecordStore<UIRenderRecord> records,
        ReadOnlySpan<UIRecordSplice> edits, ReadOnlySpan<UIRenderRecord> inserted)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        int delta = 0;
        bool insertsExplicit = false;
        foreach (UIRecordSplice edit in edits)
        {
            delta += edit.Delta;
            foreach (ref readonly UIRenderRecord record in inserted.Slice(edit.NewStart, edit.NewCount))
                insertsExplicit |= record.CompositionOwner.IsExplicit;
        }
        int oldLength = records.Length - delta;
        if (edits.IsEmpty || _immortal && !insertsExplicit)
        {
            Retain();
            return this;
        }
        if (!_immortal && oldLength != _recordCount)
            throw new ArgumentException("The spliced store does not match the segment records.", nameof(records));

        // Describe the new record order as pieces: surviving parts of old segments and inserted runs.
        List<(int Start, int Count, UICompositionOwner Owner, int Source, bool Whole)> pieces =
            new(Count + edits.Length * 2 + 1);
        int position = 0;
        int shift = 0;
        int next = 0;
        int segmentIndex = 0;
        while (true)
        {
            while (next < edits.Length && edits[next].OldStart == position)
            {
                UIRecordSplice edit = edits[next++];
                for (int index = 0; index < edit.NewCount; index++)
                {
                    UICompositionOwner owner = inserted[edit.NewStart + index].CompositionOwner;
                    int start = position + shift + index;
                    if (pieces.Count > 0 && pieces[^1] is { Source: -1 } last &&
                        last.Owner == owner && last.Start + last.Count == start)
                        pieces[^1] = last with { Count = last.Count + 1 };
                    else
                        pieces.Add((start, 1, owner, -1, false));
                }
                shift += edit.Delta;
                position += edit.OldCount;
            }
            if (position >= oldLength)
                break;
            (int segmentStart, int segmentEnd, UICompositionOwner segmentOwner) =
                OldSegment(segmentIndex, oldLength);
            while (segmentEnd <= position)
                (segmentStart, segmentEnd, segmentOwner) = OldSegment(++segmentIndex, oldLength);
            int end = Math.Min(segmentEnd, next < edits.Length ? edits[next].OldStart : oldLength);
            pieces.Add((position + shift, end - position, segmentOwner,
                _immortal ? -1 : segmentIndex, position == segmentStart && end == segmentEnd));
            position = end;
        }

        // Merge adjacent pieces of one owner into maximal runs, as a full build does.
        List<(int Start, int Count, UICompositionOwner Owner, int FirstPiece, int PieceCount)> runs =
            new(pieces.Count);
        bool anyExplicit = false;
        for (int index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            anyExplicit |= piece.Owner.IsExplicit;
            if (runs.Count > 0 && runs[^1] is var run && run.Owner == piece.Owner)
                runs[^1] = run with { Count = run.Count + piece.Count, PieceCount = run.PieceCount + 1 };
            else
                runs.Add((piece.Start, piece.Count, piece.Owner, index, 1));
        }
        if (!anyExplicit)
            return Empty;

        UICompositionSegment[] segments = new UICompositionSegment[runs.Count];
        HashSet<ulong> usedIdentities = [];
        int built = 0;
        bool transferred = false;
        try
        {
            for (; built < runs.Count; built++)
            {
                var run = runs[built];
                var first = pieces[run.FirstPiece];
                if (run.PieceCount == 1 && first.Whole && first.Source >= 0)
                {
                    UICompositionSegment prior = GetSegment(first.Source);
                    usedIdentities.Add(prior.Identity);
                    if (prior.Start == run.Start)
                    {
                        prior.Retain();
                        segments[built] = prior;
                    }
                    else
                        segments[built] = prior.WithStart(NextRevision(), run.Start);
                    continue;
                }
                ulong identity = 0;
                for (int index = run.FirstPiece; identity == 0 &&
                    index < run.FirstPiece + run.PieceCount; index++)
                    if (pieces[index].Source >= 0 && GetSegment(pieces[index].Source) is { } source &&
                        source.Owner == run.Owner && usedIdentities.Add(source.Identity))
                        identity = source.Identity;
                if (identity == 0)
                    identity = unchecked((ulong)Interlocked.Increment(ref _nextIdentity));
                segments[built] = new UICompositionSegment(identity, NextRevision(), run.Owner,
                    records, run.Start, run.Count);
            }
            UIRuntimeCounters.Add(UIWorkCounter.PublicationSegmentSlotsCopied, segments.Length);
            transferred = true;
            return TakeOwnedSegments(segments, records.Length);
        }
        catch (Exception failure)
        {
            if (transferred)
                throw;
            try { ReleaseSegments(segments.AsSpan(0, built)); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("UI composition splice and cleanup failed.",
                    failure, cleanupFailure);
            }
            throw;
        }
    }

    /// <summary>Gets an old segment span. The empty set has one implicit unowned span.</summary>
    private (int Start, int End, UICompositionOwner Owner) OldSegment(int index, int oldLength)
    {
        if (_immortal)
            return (0, oldLength, default);
        UICompositionSegment segment = GetSegment(index);
        return (segment.Start, segment.Start + segment.Count, segment.Owner);
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
        Dictionary<long, int>? previousByMembership = null;
        if (previous is not null && previous.Count > 0)
        {
            previousByMembership = new Dictionary<long, int>(previous._recordCount);
            for (int segmentIndex = 0; segmentIndex < previous.Count; segmentIndex++)
            {
                UICompositionSegment prior = previous.GetSegment(segmentIndex);
                for (int page = 0; page < prior.PageCount; page++)
                    foreach (ref readonly UIRenderRecord record in prior.GetPage(page))
                        previousByMembership[record.MembershipId] = segmentIndex;
            }
        }
        HashSet<ulong> reused = [];
        // Index lookups require one record per membership.
        HashSet<long> memberships = new(records.Length);
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
                for (int index = start; index < end; index++)
                    if (!memberships.Add(records[index].MembershipId))
                        throw new ArgumentException(
                            $"UI.Composition.DuplicateMembership: render member {records[index].MembershipId} appears twice.",
                            nameof(records));
                UICompositionSegment? prior = null;
                if (previousByMembership is not null)
                    for (int index = start; index < end; index++)
                        if (previousByMembership.TryGetValue(records[index].MembershipId, out int candidateIndex) &&
                            previous!.GetSegment(candidateIndex) is { } candidate &&
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
                start = end;
            }
            UICompositionSegment[] staged = segments.ToArray();
            transferred = true;
            return TakeOwnedSegments(staged, records.Length);
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
