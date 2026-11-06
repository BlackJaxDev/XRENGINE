namespace XREngine.Cutscenes;

public sealed partial class CutsceneResidencyPlanner
{
    /// <summary>
    /// Releases T1 and T2 data under memory pressure. First it releases the data of assets that the request
    /// window of the last plan does not need, farthest from the playhead in time first. Then it releases the
    /// data of assets in the window, farthest deadline first. T0 data never leaves while the planner is open.
    /// A release takes the held level of an asset down to T0. A later plan can request the released levels again.
    /// </summary>
    /// <param name="maxCount">The maximum number of assets to release.</param>
    /// <returns>The number of assets that released a level.</returns>
    public int Evict(int maxCount)
    {
        if (!IsOpen || maxCount <= 0)
            return 0;

        int count = 0;
        for (int i = 0; i < _assets.Length; i++)
            if (_heldTickets[i].IsValid)
                _order[count++] = i;

        _order.AsSpan(0, count).Sort(_compareEviction);

        int evicted = Math.Min(count, maxCount);
        for (int k = 0; k < evicted; k++)
        {
            int asset = _order[k];
            ResidencyTicket held = _heldTickets[asset];
            _heldTickets[asset] = ResidencyTicket.None;
            _heldLevels[asset] = 0;
            _backend.Cancel(held);
        }
        return evicted;
    }

    private int CompareEviction(int left, int right)
    {
        // The desired levels of the last plan mark the assets in the request window, also while the dwell gate is closed.
        bool leftOutside = _desiredLevels[left] <= 0;
        bool rightOutside = _desiredLevels[right] <= 0;
        if (leftOutside != rightOutside)
            return leftOutside ? -1 : 1;

        long leftDistance = DistanceFromPlayhead(_lastNeeded[left]);
        long rightDistance = DistanceFromPlayhead(_lastNeeded[right]);
        int result = rightDistance.CompareTo(leftDistance);
        return result != 0 ? result : left.CompareTo(right);
    }

    private long DistanceFromPlayhead(long time)
    {
        // An asset that no bucket needs has the time long.MaxValue. A subtraction must not overflow.
        if (time == long.MaxValue)
            return long.MaxValue;

        long distance = time - _position;
        return distance < 0 ? -distance : distance;
    }
}
