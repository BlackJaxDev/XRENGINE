using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Sets exact outgoing values on selected curves in one Undo action.</summary>
internal sealed class InspectorFloatCurveValueEdit
{
    internal const int MaximumCurves = 64;
    private readonly InspectorFloatCurveLengthSelection _selection;
    private readonly InspectorFloatCurveValueMember[] _members;
    private bool _recoveryBlocked;

    private InspectorFloatCurveValueEdit(InspectorFloatCurveLengthSelection selection,
        InspectorFloatCurveValueMember[] members, object[] reservations)
    {
        _selection = selection;
        _members = members;
        ReservationOwners = reservations;
    }

    internal object[] ReservationOwners { get; }

    internal static bool FitsCounts(int uniqueCurves, long existingKeys)
        => uniqueCurves is > 1 and <= MaximumCurves && existingKeys >= uniqueCurves &&
            existingKeys <= InspectorFloatCurveLengthSelection.MaximumMembers;

    internal static bool CanSetWithinLimits(InspectorFloatCurveLengthSelection selection)
    {
        long keys = 0;
        foreach (InspectorFloatCurveSource source in selection.Curves)
        {
            if (source.Track.Count < 0) return false;
            keys += source.Track.Count;
        }
        return FitsCounts(selection.Curves.Length, keys);
    }

    internal static bool TryCapture(InspectorFloatCurveLengthSelection selection,
        IReadOnlyList<FloatKeyframe> targets, float value,
        out InspectorFloatCurveValueEdit? edit)
    {
        edit = null;
        if (targets is null || targets.Count != selection.Curves.Length ||
            !float.IsFinite(value) || !CanSetWithinLimits(selection) ||
            !selection.IsCurrent())
            return false;
        int remaining = InspectorFloatCurveLengthSelection.MaximumMembers;
        bool hasChange = false;
        List<InspectorFloatCurveValueMember> members = [];
        HashSet<BaseKeyframeTrack> tracks = new(ReferenceEqualityComparer.Instance);
        HashSet<FloatKeyframe> selectedKeys = new(ReferenceEqualityComparer.Instance);
        HashSet<object> reservations = new(selection.ReservationOwners,
            ReferenceEqualityComparer.Instance);
        for (int index = 0; index < selection.Curves.Length; index++)
        {
            InspectorFloatCurveSource source = selection.Curves[index];
            FloatKeyframe? key = targets[index];
            if (key is null || !tracks.Add(source.Track) || !selectedKeys.Add(key) ||
                !InspectorFloatCurveValueMember.TryCapture(source, key, value,
                    remaining, out InspectorFloatCurveValueMember? member) || member is null)
                return false;
            remaining -= member.ExistingKeys.Count;
            foreach (Keyframe existing in member.ExistingKeys)
                reservations.Add(existing);
            hasChange |= member.HasChange;
            members.Add(member);
        }
        if (!hasChange) return false;
        edit = new(selection, [.. members], [.. reservations]);
        if (edit.Matches(after: false)) return true;
        edit = null;
        return false;
    }

    internal bool Matches(bool after)
        => !_recoveryBlocked && MatchesAuthoredState(after);

    private bool MatchesAuthoredState(bool after)
    {
        if (!_selection.IsCurrent()) return false;
        foreach (InspectorFloatCurveValueMember member in _members)
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
        if (_members.Any(member => !member.CanStartOperation) ||
            !MatchesPhase(phases, exactGenerations, notifications))
            throw new InvalidOperationException("Selected animation values changed before the edit.");
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
                if (_members[current].HasChange) notifications[current]++;
                phases[current] = forward;
                if (!MatchesPhase(phases, exactGenerations, notifications))
                    throw new InvalidOperationException("A selected animation peer changed during notification.");
            }
        }
        catch
        {
            // The complete authored state commits before the Changed observer runs.
            if (current >= 0 && _members[current].HasChange &&
                _members[current].ExactGeneration == exactGenerations[current] &&
                _members[current].NotificationRevision == notifications[current] + 1 &&
                _members[current].CanNotify && _members[current].Matches(forward))
            {
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
        if (!_selection.IsCurrent()) return false;
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
            if (!_selection.IsCurrent() || !_members[index].CanNotify ||
                _members[index].ExactGeneration != exactGenerations[index] ||
                _members[index].NotificationRevision != notifications[index] ||
                !_members[index].Matches(forward)) return;
            try
            {
                _members[index].Transition(!forward, exactGenerations[index],
                    notifications[index]);
            }
            catch { return; }
            notifications[index]++;
            phases[index] = !forward;
            if (!MatchesPhase(phases, exactGenerations, notifications)) return;
        }
    }
}
