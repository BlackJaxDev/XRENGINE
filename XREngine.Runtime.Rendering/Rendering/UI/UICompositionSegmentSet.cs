using System.Threading;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.UI;

/// <summary>Owns immutable composition spans and their membership lookup.</summary>
internal sealed class UICompositionSegmentSet
{
    private static long _nextIdentity;
    private static long _nextRevision;
    private readonly UICompositionSegment[] _segments;
    private readonly Dictionary<long, int> _byMembership;
    private readonly bool _immortal;
    private int _claims = 1;
    private int _retired;

    internal static UICompositionSegmentSet Empty { get; } = new([], [], true);

    private UICompositionSegmentSet(UICompositionSegment[] segments,
        Dictionary<long, int> byMembership, bool immortal = false)
    {
        _segments = segments;
        _byMembership = byMembership;
        _immortal = immortal;
    }

    internal bool ResourcesRetired => !_immortal && Volatile.Read(ref _retired) != 0;
    internal ReadOnlySpan<UICompositionSegment> Segments
    {
        get
        {
            ObjectDisposedException.ThrowIf(ResourcesRetired, this);
            return _segments;
        }
    }

    internal bool TryGet(long membershipId, out UICompositionSegment segment)
    {
        ObjectDisposedException.ThrowIf(ResourcesRetired, this);
        if (_byMembership.TryGetValue(membershipId, out int index))
        {
            segment = _segments[index];
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
        ReleaseSegments(_segments);
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

    private bool TryUpdateSameTopology(UIRecordStore<UIRenderRecord> records,
        out UICompositionSegmentSet updated)
    {
        updated = null!;
        if (_segments.Length == 0 || _byMembership.Count != records.Length)
            return false;
        int offset = 0;
        foreach (UICompositionSegment segment in _segments)
        {
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
        foreach (UICompositionSegment prior in _segments)
        {
            anyChanged |= !prior.ContentEquals(records, offset, prior.Count);
            offset += prior.Count;
        }
        if (!anyChanged)
        {
            Retain();
            updated = this;
            return true;
        }

        UICompositionSegment[] changed = new UICompositionSegment[_segments.Length];
        int retained = 0;
        offset = 0;
        try
        {
            for (; retained < _segments.Length; retained++)
            {
                UICompositionSegment prior = _segments[retained];
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
            updated = new UICompositionSegmentSet(changed, _byMembership);
            return true;
        }
        catch (Exception failure)
        {
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
                            previous._segments[candidateIndex] is { } candidate &&
                            candidate.Owner == owner && reused.Add(candidate.Identity))
                        {
                            prior = candidate;
                            break;
                        }
                UICompositionSegment segment;
                if (prior is not null && prior.ContentEquals(records, start, end - start))
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
            return new UICompositionSegmentSet(segments.ToArray(), byMembership);
        }
        catch (Exception failure)
        {
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
