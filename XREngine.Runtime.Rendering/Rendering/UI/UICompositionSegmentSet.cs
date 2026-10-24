namespace XREngine.Rendering.UI;

/// <summary>Shares unchanged layer spans while preserving every painter-order boundary.</summary>
internal sealed class UICompositionSegmentSet
{
    private static long _nextIdentity;
    private static long _nextRevision;
    private readonly UICompositionSegment[] _segments;
    private readonly Dictionary<long, int> _byMembership;
    internal static UICompositionSegmentSet Empty { get; } = new([], []);

    private UICompositionSegmentSet(UICompositionSegment[] segments,
        Dictionary<long, int> byMembership)
    {
        _segments = segments;
        _byMembership = byMembership;
    }

    internal ReadOnlySpan<UICompositionSegment> Segments => _segments;

    private static ulong NextRevision() => unchecked((ulong)Interlocked.Increment(ref _nextRevision));

    private bool TryUpdateSameTopology(ReadOnlySpan<UIRenderRecord> records,
        out UICompositionSegmentSet updated)
    {
        updated = this;
        if (_segments.Length == 0 || _byMembership.Count != records.Length)
            return false;
        int offset = 0;
        foreach (UICompositionSegment segment in _segments)
        {
            ReadOnlySpan<UIRenderRecord> prior = segment.Records;
            for (int index = 0; index < prior.Length; index++)
            {
                ref readonly UIRenderRecord record = ref records[offset + index];
                if (record.MembershipId != prior[index].MembershipId || record.CompositionOwner != segment.Owner)
                    return false;
            }
            offset += prior.Length;
        }
        UICompositionSegment[]? changed = null;
        offset = 0;
        for (int index = 0; index < _segments.Length; index++)
        {
            UICompositionSegment prior = _segments[index];
            ReadOnlySpan<UIRenderRecord> span = records.Slice(offset, prior.Records.Length);
            if (!span.SequenceEqual(prior.Records))
            {
                changed ??= (UICompositionSegment[])_segments.Clone();
                changed[index] = new UICompositionSegment(prior.Identity, NextRevision(), prior.Owner, span);
            }
            offset += span.Length;
        }
        if (changed is not null)
            updated = new UICompositionSegmentSet(changed, _byMembership);
        return true;
    }

    internal static UICompositionSegmentSet Build(ReadOnlySpan<UIRenderRecord> records,
        UICompositionSegmentSet? previous)
    {
        if (previous is not null && previous.TryUpdateSameTopology(records, out var updated))
            return updated;

        bool hasExplicitOwner = false;
        foreach (ref readonly UIRenderRecord record in records)
            if (record.CompositionOwner.IsExplicit)
            {
                hasExplicitOwner = true;
                break;
            }
        if (!hasExplicitOwner)
            return Empty;

        List<UICompositionSegment> segments = [];
        Dictionary<long, int> byMembership = new(records.Length);
        HashSet<ulong> reused = [];
        int start = 0;
        while (start < records.Length)
        {
            UICompositionOwner owner = records[start].CompositionOwner;
            int end = start + 1;
            while (end < records.Length && records[end].CompositionOwner == owner)
                end++;
            ReadOnlySpan<UIRenderRecord> span = records[start..end];
            UICompositionSegment? prior = null;
            if (previous is not null)
                foreach (ref readonly UIRenderRecord record in span)
                    if (previous._byMembership.TryGetValue(record.MembershipId, out int candidateIndex) &&
                        previous._segments[candidateIndex] is { } candidate &&
                        candidate.Owner == owner && reused.Add(candidate.Identity))
                    {
                        prior = candidate;
                        break;
                    }
            UICompositionSegment segment;
            if (prior is not null && span.SequenceEqual(prior.Records))
                segment = prior;
            else
            {
                ulong identity = prior?.Identity ?? unchecked((ulong)Interlocked.Increment(ref _nextIdentity));
                segment = new UICompositionSegment(identity, NextRevision(), owner, span);
            }
            segments.Add(segment);
            foreach (ref readonly UIRenderRecord record in span)
                byMembership.Add(record.MembershipId, segments.Count - 1);
            start = end;
        }
        return new UICompositionSegmentSet(segments.ToArray(), byMembership);
    }
}
