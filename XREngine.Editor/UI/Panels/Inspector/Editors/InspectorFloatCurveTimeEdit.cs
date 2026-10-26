using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Moves exact marked keys on selected curves in one Undo action.</summary>
internal sealed class InspectorFloatCurveTimeEdit
{
    internal const int MaximumCurves = 64;
    private readonly InspectorFloatCurveEditBatch _batch;

    private InspectorFloatCurveTimeEdit(InspectorFloatCurveLengthSelection selection,
        InspectorFloatCurveTimeMember[] members, object[] reservations)
    {
        _batch = new(selection.IsCurrent, members);
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
        out InspectorFloatCurveTimeEdit? edit)
    {
        edit = null;
        if (targets is null || targets.Count != selection.Curves.Length ||
            !float.IsFinite(value) || value < 0 || !CanSetWithinLimits(selection) || !selection.IsCurrent())
            return false;
        int remaining = InspectorFloatCurveLengthSelection.MaximumMembers;
        bool hasChange = false;
        List<InspectorFloatCurveTimeMember> members = [];
        HashSet<BaseKeyframeTrack> tracks = new(ReferenceEqualityComparer.Instance);
        HashSet<FloatKeyframe> selectedKeys = new(ReferenceEqualityComparer.Instance);
        HashSet<object> reservations = new(selection.ReservationOwners,
            ReferenceEqualityComparer.Instance);
        for (int index = 0; index < selection.Curves.Length; index++)
        {
            InspectorFloatCurveSource source = selection.Curves[index];
            FloatKeyframe? key = targets[index];
            if (key is null || !tracks.Add(source.Track) || !selectedKeys.Add(key) ||
                !InspectorFloatCurveTimeMember.TryCapture(source, key, value,
                    remaining, out InspectorFloatCurveTimeMember? member) || member is null)
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

    internal bool Matches(bool after) => _batch.Matches(after);

    internal void Apply(bool forward) => _batch.Apply(forward);
}
