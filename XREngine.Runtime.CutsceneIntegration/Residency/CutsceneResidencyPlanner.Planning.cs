namespace XREngine.Cutscenes;

public sealed partial class CutsceneResidencyPlanner
{
    /// <summary>The largest window span in flicks. It keeps the window ends away from an overflow at a very large rate.</summary>
    private const long MaxWindowSpan = long.MaxValue / 4;

    private int _currentBucket = -1;
    private int _windowFirst;
    private int _windowLast = -1;
    private long _windowAhead;
    private bool _windowForward = true;

    private int FindBucket(long position)
    {
        int index = Array.BinarySearch(_bucketStarts, position);
        return index >= 0 ? index : ~index - 1;
    }

    private void FindWindow(long position, double rate, out int first, out int last)
    {
        // A pause uses the forward window of rate 1, so that a pause does not cancel the prefetch.
        double effectiveRate = rate == 0.0 ? 1.0 : rate;
        double scaled = _lookAhead.Flicks * Math.Abs(effectiveRate);
        long ahead = scaled >= MaxWindowSpan ? MaxWindowSpan : (long)scaled;
        long behind = Math.Min(_lookBehind.Flicks, MaxWindowSpan);
        _windowAhead = ahead;
        _windowForward = effectiveRate > 0.0;

        long low;
        long high;
        if (_windowForward)
        {
            low = position - behind;
            high = position + ahead;
        }
        else
        {
            low = position - ahead;
            high = position + behind;
        }

        // The first bucket that ends after the low edge. A bucket end is not part of the bucket.
        int firstIndex = Array.BinarySearch(_bucketEnds, low);
        first = firstIndex >= 0 ? firstIndex + 1 : ~firstIndex;

        // The last bucket that starts at or before the high edge.
        int lastIndex = Array.BinarySearch(_bucketStarts, high);
        last = lastIndex >= 0 ? lastIndex : ~lastIndex - 1;

        if (first > last)
        {
            first = 0;
            last = -1;
        }
    }

    private void Replan()
    {
        _dirty = false;

        int assetCount = _assets.Length;
        Array.Clear(_desiredLevels);
        Array.Fill(_untilNeeded, long.MaxValue);
        Array.Clear(_coverages);

        // The desired levels record the whole window, also while the dwell gate is closed. Eviction uses them.
        long position = _position;
        for (int b = _windowFirst; b <= _windowLast; b++)
        {
            // The nearest time of the bucket to the playhead. It is the playhead itself for the current bucket.
            long deadline = Math.Clamp(position, _bucketStarts[b], _bucketEnds[b] - 1);
            long until = Math.Abs(deadline - position);

            // A bucket behind the playhead sorts after every bucket ahead of it in the direction of travel.
            bool behind = _windowForward ? deadline < position : deadline > position;
            if (behind)
                until += _windowAhead + 1;

            ResidencyEntry[] entries = _buckets[b].Entries;
            for (int e = 0; e < entries.Length; e++)
            {
                ResidencyEntry entry = entries[e];
                int asset = entry.AssetIndex;
                if (entry.TargetLevel > _desiredLevels[asset])
                    _desiredLevels[asset] = entry.TargetLevel;
                if (until < _untilNeeded[asset])
                {
                    _untilNeeded[asset] = until;
                    _deadlines[asset] = deadline;
                }
                if (entry.ScreenCoverage > _coverages[asset])
                    _coverages[asset] = entry.ScreenCoverage;
            }
        }

        bool upperAllowed = !_scrubbing || _dwellOpen;
        int count = 0;
        for (int i = 0; i < assetCount; i++)
        {
            int desired = _desiredLevels[i];
            if (desired > 0)
                _lastNeeded[i] = _deadlines[i];
            if (!upperAllowed)
                desired = 0;

            if (desired <= 0)
            {
                CancelPendingUnlessComplete(i);
                continue;
            }

            // Keep running work of this generation that the window still needs. Its completion becomes the hold,
            // and the next plan requests the level after it. This also keeps work that completed after the drain.
            if (_pendingTickets[i].IsValid)
            {
                if (_pendingLevels[i] <= desired && _pendingGenerations[i] == _generation)
                    continue;

                CancelPendingUnlessComplete(i);
                if (_pendingTickets[i].IsValid)
                    continue;
            }

            int resident = Math.Max((int)_backend.GetLevel(_assets[i]), _heldLevels[i]);
            if (desired <= resident)
                continue;

            _requestLevels[i] = Math.Max(resident + 1, 1);
            _order[count++] = i;
        }

        _order.AsSpan(0, count).Sort(_compareRequests);

        for (int k = 0; k < count && !_closed; k++)
        {
            int asset = _order[k];
            ResidencyRequest request = new(_assets[asset], _requestLevels[asset], new CutsceneTime(_deadlines[asset]), _coverages[asset], _generation);
            ResidencyTicket ticket = _backend.Request(in request);
            if (!ticket.IsValid)
                continue;

            _pendingTickets[asset] = ticket;
            _pendingLevels[asset] = request.TargetLevel;
            _pendingGenerations[asset] = _generation;
            _ticketAssets[ticket.Id] = asset;
        }
    }

    private void CancelStalePending()
    {
        for (int i = 0; i < _assets.Length; i++)
            if (_pendingTickets[i].IsValid && _pendingGenerations[i] < _generation)
                CancelPendingUnlessComplete(i);
    }

    /// <summary>
    /// Cancels the running work of an asset. If the backend already reports the level of that work, the work
    /// can be complete with its completion not yet applied. A cancel then releases the data, so the planner keeps
    /// the ticket in the current generation, and its completion becomes the hold.
    /// </summary>
    private void CancelPendingUnlessComplete(int asset)
    {
        ResidencyTicket ticket = _pendingTickets[asset];
        if (!ticket.IsValid)
            return;

        if ((int)_backend.GetLevel(_assets[asset]) >= _pendingLevels[asset])
        {
            _pendingGenerations[asset] = _generation;
            return;
        }

        _pendingTickets[asset] = ResidencyTicket.None;
        _ticketAssets.Remove(ticket.Id);
        _backend.Cancel(ticket);
    }

    private int CompareRequests(int left, int right)
    {
        int result = _requestLevels[left].CompareTo(_requestLevels[right]);
        if (result != 0)
            return result;

        result = _untilNeeded[left].CompareTo(_untilNeeded[right]);
        if (result != 0)
            return result;

        result = _coverages[right].CompareTo(_coverages[left]);
        return result != 0 ? result : left.CompareTo(right);
    }
}
