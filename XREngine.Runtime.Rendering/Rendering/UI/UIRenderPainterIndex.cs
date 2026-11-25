namespace XREngine.Rendering.UI;

/// <summary>
/// Maps each render membership to its position in one immutable painter order. A canvas
/// publication stores its render records in this order, so a membership lookup is a binary
/// search over the record store. Publications with the same painter order share one index.
/// </summary>
internal sealed class UIRenderPainterIndex
{
    private readonly Dictionary<long, int> _positions;

    private UIRenderPainterIndex(Dictionary<long, int> positions, ulong revision)
    {
        _positions = positions;
        Revision = revision;
    }

    /// <summary>Gets the painter-order revision that this index describes.</summary>
    internal ulong Revision { get; }

    /// <summary>Copies a painter order. A repeated membership keeps its first position.</summary>
    internal static UIRenderPainterIndex Create(IReadOnlyList<long> painterOrder, ulong revision)
    {
        ArgumentNullException.ThrowIfNull(painterOrder);
        Dictionary<long, int> positions = new(painterOrder.Count);
        for (int index = 0; index < painterOrder.Count; index++)
            positions.TryAdd(painterOrder[index], index);
        return new UIRenderPainterIndex(positions, revision);
    }

    internal bool TryGetPosition(long membershipId, out int position)
        => _positions.TryGetValue(membershipId, out position);

    /// <summary>
    /// Gets the first store index whose record has a painter position at or after
    /// <paramref name="position"/>. Returns -1 when a probed record is not in this order.
    /// </summary>
    internal int LowerBound(UIRecordStore<UIRenderRecord> records, int position)
    {
        int low = 0;
        int high = records.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (!_positions.TryGetValue(records[middle].MembershipId, out int probed))
                return -1;
            if (probed < position)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>Finds the store index of a membership, or returns false.</summary>
    internal bool TryFind(UIRecordStore<UIRenderRecord> records, long membershipId, out int index)
    {
        index = -1;
        if (!_positions.TryGetValue(membershipId, out int position))
            return false;
        int found = LowerBound(records, position);
        if (found < 0 || found >= records.Length || records[found].MembershipId != membershipId)
            return false;
        index = found;
        return true;
    }

    /// <summary>Checks that every record is in this order and that positions strictly ascend.</summary>
    internal bool Orders(ReadOnlySpan<UIRenderRecord> records)
    {
        int previous = -1;
        foreach (ref readonly UIRenderRecord record in records)
        {
            if (!_positions.TryGetValue(record.MembershipId, out int position) || position <= previous)
                return false;
            previous = position;
        }
        return true;
    }
}
