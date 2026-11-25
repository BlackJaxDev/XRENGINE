using System.Reflection;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine.Editor;

/// <summary>Retains every selected owner while deduplicating exact curve mutations.</summary>
internal sealed class InspectorFloatCurveLengthSelection
{
    internal const int MaximumMembers = 100_000;
    private readonly InspectorFloatCurveSource[] _owners;

    private InspectorFloatCurveLengthSelection(InspectorFloatCurveSource[] owners,
        InspectorFloatCurveSource[] curves)
    {
        _owners = owners;
        Curves = curves;
        HashSet<XRAsset> documents = new(ReferenceEqualityComparer.Instance);
        HashSet<object> reservations = new(ReferenceEqualityComparer.Instance);
        foreach (InspectorFloatCurveSource owner in owners)
        {
            foreach (XRAsset document in owner.Documents) documents.Add(document);
            foreach (object item in owner.ReservationOwners) reservations.Add(item);
        }
        Documents = [.. documents];
        ReservationOwners = [.. reservations];
    }

    internal InspectorFloatCurveSource[] Curves { get; }
    internal XRAsset[] Documents { get; }
    internal object[] ReservationOwners { get; }
    internal bool HasDistinctCurves => Curves.Length > 1;

    internal float? CommonLength
    {
        get
        {
            if (Curves.Length == 0) return null;
            float length = Curves[0].Curve.LengthInSeconds;
            for (int i = 1; i < Curves.Length; i++)
                if (Curves[i].Curve.LengthInSeconds != length) return null;
            return length;
        }
    }

    internal static bool TryCapture(PropertyInfo property, object?[]? targets,
        out InspectorFloatCurveLengthSelection? selection, out string reason,
        InspectorFloatTransitionOwnership? transitionPath = null)
    {
        selection = null;
        reason = "No valid float curves are selected.";
        if (targets is null || targets.Length == 0 || targets.Length > MaximumMembers)
            return false;
        HashSet<object> seenOwners = new(ReferenceEqualityComparer.Instance);
        HashSet<PropAnimFloat> seenCurves = new(ReferenceEqualityComparer.Instance);
        List<InspectorFloatCurveSource> owners = [];
        List<InspectorFloatCurveSource> curves = [];
        int keys = 0;
        foreach (object? target in targets)
        {
            if (target is null) return false;
            if (!seenOwners.Add(target)) continue;
            if (!InspectorFloatCurveSource.TryCapture(property, [target],
                    out InspectorFloatCurveSource? source, out reason, transitionPath) ||
                source is null)
                return false;
            owners.Add(source);
            if (seenCurves.Add(source.Curve))
            {
                if (source.Track.Count > MaximumMembers - keys)
                {
                    reason = "Selected curves exceed the supported length edit size.";
                    return false;
                }
                keys += source.Track.Count;
                curves.Add(source);
            }
        }
        if (curves.Count == 0) return false;
        selection = new([.. owners], [.. curves]);
        if (selection.IsCurrent())
        {
            reason = string.Empty;
            return true;
        }
        selection = null;
        reason = "The selected curve owners changed during capture.";
        return false;
    }

    internal bool IsCurrentFast()
    {
        if (!WithinBounds()) return false;
        foreach (InspectorFloatCurveSource owner in _owners)
            if (!owner.IsCurrentFast()) return false;
        return true;
    }

    internal bool IsCurrent()
    {
        if (!WithinBounds()) return false;
        foreach (InspectorFloatCurveSource owner in _owners)
            if (!owner.IsCurrent()) return false;
        return true;
    }

    private bool WithinBounds()
    {
        int remaining = MaximumMembers;
        foreach (InspectorFloatCurveSource source in Curves)
        {
            if (source.Track.Count > remaining) return false;
            remaining -= source.Track.Count;
        }
        return true;
    }
}
