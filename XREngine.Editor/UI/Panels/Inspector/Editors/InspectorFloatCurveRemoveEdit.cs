using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Removes one exact key from each selected curve in one Undo action.</summary>
internal sealed class InspectorFloatCurveRemoveEdit
{
    internal const int MaximumCurves = 64;
    private readonly InspectorFloatCurveLengthSelection _selection;
    private readonly InspectorFloatCurveRemoveMember[] _members;
    private bool _recoveryBlocked;

    private InspectorFloatCurveRemoveEdit(InspectorFloatCurveLengthSelection selection,
        InspectorFloatCurveRemoveMember[] members, object[] reservations)
    {
        _selection = selection;
        _members = members;
        ReservationOwners = reservations;
    }

    internal object[] ReservationOwners { get; }

    internal static bool FitsCounts(int uniqueCurves, long existingKeys)
        => uniqueCurves is > 1 and <= MaximumCurves && existingKeys >= uniqueCurves &&
            existingKeys <= InspectorFloatCurveLengthSelection.MaximumMembers;

    internal static bool CanRemoveWithinLimits(InspectorFloatCurveLengthSelection selection)
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
        IReadOnlyList<FloatKeyframe> targets, out InspectorFloatCurveRemoveEdit? edit)
    {
        edit = null;
        if (targets is null || targets.Count != selection.Curves.Length ||
            !CanRemoveWithinLimits(selection) || !selection.IsCurrent())
            return false;
        int remaining = InspectorFloatCurveLengthSelection.MaximumMembers;
        List<InspectorFloatCurveRemoveMember> members = [];
        HashSet<BaseKeyframeTrack> tracks = new(ReferenceEqualityComparer.Instance);
        HashSet<FloatKeyframe> selectedKeys = new(ReferenceEqualityComparer.Instance);
        HashSet<object> reservations = new(selection.ReservationOwners,
            ReferenceEqualityComparer.Instance);
        for (int index = 0; index < selection.Curves.Length; index++)
        {
            InspectorFloatCurveSource source = selection.Curves[index];
            FloatKeyframe? key = targets[index];
            if (key is null || !tracks.Add(source.Track) ||
                !selectedKeys.Add(key) ||
                !InspectorFloatCurveRemoveMember.TryCapture(source, key,
                    remaining, out InspectorFloatCurveRemoveMember? member) || member is null)
                return false;
            remaining -= member.ExistingKeys.Count;
            foreach (Keyframe existing in member.ExistingKeys)
                reservations.Add(existing);
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
        foreach (InspectorFloatCurveRemoveMember member in _members)
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
            // Do not let the outer transaction adopt callback-restored values.
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
