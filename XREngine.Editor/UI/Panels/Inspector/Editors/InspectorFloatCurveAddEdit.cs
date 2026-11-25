using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Applies one key to each selected curve under one exact Undo action.</summary>
internal sealed class InspectorFloatCurveAddEdit
{
    internal const int MaximumCurves = 64;
    private readonly InspectorFloatCurveLengthSelection _selection;
    private readonly InspectorFloatCurveAddMember[] _members;
    private bool _recoveryBlocked;

    private InspectorFloatCurveAddEdit(InspectorFloatCurveLengthSelection selection,
        InspectorFloatCurveAddMember[] members, object[] reservations)
    {
        _selection = selection;
        _members = members;
        ReservationOwners = reservations;
    }

    internal object[] ReservationOwners { get; }

    internal static bool FitsCounts(int uniqueCurves, long existingKeys)
        => uniqueCurves is > 1 and <= MaximumCurves && existingKeys >= 0 &&
            existingKeys <= InspectorFloatCurveLengthSelection.MaximumMembers - uniqueCurves;

    internal static bool CanAddWithinLimits(InspectorFloatCurveLengthSelection selection)
    {
        if (!FitsCounts(selection.Curves.Length, 0)) return false;
        long keys = 0;
        foreach (InspectorFloatCurveSource source in selection.Curves)
        {
            if (source.Track.Count < 0) return false;
            keys += source.Track.Count;
        }
        return FitsCounts(selection.Curves.Length, keys);
    }

    internal static bool TryCapture(InspectorFloatCurveLengthSelection selection,
        float second, out InspectorFloatCurveAddEdit? edit)
    {
        edit = null;
        if (!CanAddWithinLimits(selection) || !selection.IsCurrent() ||
            !float.IsFinite(second) || second < 0)
            return false;
        int remaining = InspectorFloatCurveLengthSelection.MaximumMembers -
            selection.Curves.Length;
        List<InspectorFloatCurveAddMember> members = [];
        HashSet<BaseKeyframeTrack> tracks = new(ReferenceEqualityComparer.Instance);
        HashSet<object> reservations = new(selection.ReservationOwners,
            ReferenceEqualityComparer.Instance);
        foreach (InspectorFloatCurveSource source in selection.Curves)
        {
            if (!tracks.Add(source.Track) ||
                !InspectorFloatCurveAddMember.TryCapture(source, second,
                    remaining, out InspectorFloatCurveAddMember? member) || member is null)
                return false;
            remaining -= member.ExistingKeys.Count;
            foreach (Keyframe key in member.ExistingKeys)
                reservations.Add(key);
            reservations.Add(member.Key);
            members.Add(member);
        }
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
        foreach (InspectorFloatCurveAddMember member in _members)
            if (!member.Matches(after)) return false;
        return true;
    }

    internal void Apply(bool forward)
    {
        bool[] phases = new bool[_members.Length];
        ulong[] generations = new ulong[_members.Length];
        for (int index = 0; index < _members.Length; index++)
            generations[index] = _members[index].ExactGeneration;
        if (!forward) Array.Fill(phases, true);
        if (!MatchesPhase(phases, generations))
            throw new InvalidOperationException("Selected animation keys changed before the edit.");
        int current = -1;
        try
        {
            for (int step = 0; step < _members.Length; step++)
            {
                current = forward ? step : _members.Length - step - 1;
                if (!MatchesPhase(phases, generations))
                    throw new InvalidOperationException("A selected animation peer changed before the next edit.");
                _members[current].Transition(forward, generations[current]);
                generations[current]++;
                phases[current] = forward;
                if (!MatchesPhase(phases, generations))
                    throw new InvalidOperationException("A selected animation peer changed during notification.");
            }
        }
        catch
        {
            // A track can commit before its Changed callback throws.
            if (current >= 0 &&
                _members[current].ExactGeneration == generations[current] + 1 &&
                _members[current].Matches(forward))
            {
                generations[current]++;
                phases[current] = forward;
            }
            Compensate(phases, generations, forward);
            // The outer transaction must not treat callback-restored values as
            // authority to start a new reverse operation after an incomplete edit.
            if (!MatchesAuthoredState(!forward))
                _recoveryBlocked = true;
            throw;
        }
    }

    private bool MatchesPhase(bool[] phases, ulong[] generations)
    {
        if (!_selection.IsCurrent()) return false;
        for (int index = 0; index < _members.Length; index++)
            if (_members[index].ExactGeneration != generations[index] ||
                !_members[index].Matches(phases[index])) return false;
        return true;
    }

    private void Compensate(bool[] phases, ulong[] generations, bool forward)
    {
        for (int step = _members.Length - 1; step >= 0; step--)
        {
            int index = forward ? step : _members.Length - step - 1;
            if (phases[index] != forward) continue;
            if (!_selection.IsCurrent() ||
                _members[index].ExactGeneration != generations[index] ||
                !_members[index].Matches(forward)) return;
            try { _members[index].Transition(!forward, generations[index]); }
            catch { return; }
            generations[index]++;
            phases[index] = !forward;
            if (!MatchesPhase(phases, generations)) return;
        }
    }
}
