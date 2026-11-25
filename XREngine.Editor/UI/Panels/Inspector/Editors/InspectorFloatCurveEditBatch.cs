namespace XREngine.Editor;

/// <summary>Applies and compensates exact edits across explicitly selected curves.</summary>
internal sealed class InspectorFloatCurveEditBatch(
    Func<bool> sourceCurrent, IInspectorFloatCurveEditMember[] members)
{
    private readonly Func<bool> _sourceCurrent = sourceCurrent;
    private readonly IInspectorFloatCurveEditMember[] _members = members;
    private bool _recoveryBlocked;

    internal bool Matches(bool after)
        => !_recoveryBlocked && MatchesAuthoredState(after);

    private bool MatchesAuthoredState(bool after)
    {
        if (!_sourceCurrent()) return false;
        foreach (IInspectorFloatCurveEditMember member in _members)
            if (!member.Matches(after)) return false;
        return true;
    }

    internal void Apply(bool forward)
    {
        bool[] phases = new bool[_members.Length];
        ulong[] exactGenerations = new ulong[_members.Length];
        ulong[] notifications = new ulong[_members.Length];
        for (int index = 0; index < _members.Length; index++)
        {
            exactGenerations[index] = _members[index].ExactGeneration;
            notifications[index] = _members[index].NotificationRevision;
        }
        if (!forward) Array.Fill(phases, true);
        if (_members.Any(member => !member.CanReserveTransitions(2)) ||
            !MatchesPhase(phases, exactGenerations, notifications))
            throw new InvalidOperationException("Selected animation keys changed before the edit.");
        int current = -1;
        try
        {
            for (int step = 0; step < _members.Length; step++)
            {
                current = forward ? step : _members.Length - step - 1;
                if (!MatchesPhase(phases, exactGenerations, notifications))
                    throw new InvalidOperationException("A selected animation peer changed before the next edit.");
                _members[current].Transition(forward, exactGenerations[current],
                    notifications[current]);
                if (_members[current].HasChange)
                {
                    exactGenerations[current] += _members[current].ExactGenerationDelta;
                    notifications[current]++;
                }
                phases[current] = forward;
                if (!MatchesPhase(phases, exactGenerations, notifications))
                    throw new InvalidOperationException("A selected animation peer changed during notification.");
            }
        }
        catch
        {
            // The complete authored state commits before the Changed observer runs.
            if (current >= 0 && _members[current].HasChange &&
                exactGenerations[current] <= ulong.MaxValue - _members[current].ExactGenerationDelta &&
                notifications[current] < ulong.MaxValue &&
                _members[current].ExactGeneration == exactGenerations[current] + _members[current].ExactGenerationDelta &&
                _members[current].NotificationRevision == notifications[current] + 1 &&
                _members[current].CanNotify && _members[current].Matches(forward))
            {
                exactGenerations[current] += _members[current].ExactGenerationDelta;
                notifications[current]++;
                phases[current] = forward;
            }
            Compensate(phases, exactGenerations, notifications, forward);
            // A failed operation must not recapture callback revisions in the outer transaction.
            if (!MatchesAuthoredState(!forward))
                _recoveryBlocked = true;
            throw;
        }
    }

    private bool MatchesPhase(bool[] phases, ulong[] exactGenerations,
        ulong[] notifications)
    {
        if (!_sourceCurrent()) return false;
        for (int index = 0; index < _members.Length; index++)
            if (!_members[index].CanNotify ||
                _members[index].ExactGeneration != exactGenerations[index] ||
                _members[index].NotificationRevision != notifications[index] ||
                !_members[index].Matches(phases[index])) return false;
        return true;
    }

    private void Compensate(bool[] phases, ulong[] exactGenerations,
        ulong[] notifications, bool forward)
    {
        for (int step = _members.Length - 1; step >= 0; step--)
        {
            int index = forward ? step : _members.Length - step - 1;
            if (phases[index] != forward || !_members[index].HasChange) continue;
            if (!_sourceCurrent() || !_members[index].CanNotify ||
                _members[index].ExactGeneration != exactGenerations[index] ||
                _members[index].NotificationRevision != notifications[index] ||
                !_members[index].Matches(forward)) return;
            try
            {
                _members[index].Transition(!forward, exactGenerations[index],
                    notifications[index]);
            }
            catch { return; }
            exactGenerations[index] += _members[index].ExactGenerationDelta;
            notifications[index]++;
            phases[index] = !forward;
            if (!MatchesPhase(phases, exactGenerations, notifications)) return;
        }
    }
}
