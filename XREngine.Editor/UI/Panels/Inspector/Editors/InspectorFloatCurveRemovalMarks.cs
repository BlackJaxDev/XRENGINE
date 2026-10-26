using XREngine.Animation;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Keeps explicit, exact removal targets separate from table focus.</summary>
internal sealed class InspectorFloatCurveRemovalMarks
{
    internal const int MaximumCurves = 64;
    private readonly PropAnimFloat[] _curves;
    private readonly ObjectConstructionIdentity[] _identities;
    private readonly KeyframeTrack<FloatKeyframe>[] _tracks;
    private readonly Func<bool> _ownsSources;
    private readonly (FloatKeyframe Key, float Second, int Frame,
        VectorKeyframeAuthoredState<float> Payload, ulong Generation)?[] _marks;

    internal InspectorFloatCurveRemovalMarks(IReadOnlyList<PropAnimFloat> curves, Func<bool> ownsSources)
    {
        if (curves.Count is < 2 or > MaximumCurves ||
            curves.Distinct(ReferenceEqualityComparer.Instance).Count() != curves.Count)
            throw new ArgumentException("Removal requires two to sixty-four distinct curves.", nameof(curves));
        _curves = [.. curves];
        _identities = _curves.Select(ObjectConstructionIdentity.Capture).ToArray();
        _tracks = _curves.Select(curve => curve.Keyframes).ToArray();
        _ownsSources = ownsSources;
        _marks = new (FloatKeyframe, float, int, VectorKeyframeAuthoredState<float>, ulong)?[curves.Count];
    }

    internal ulong IntentRevision { get; private set; }
    internal int Count => _curves.Length;
    internal int MarkedCount => _marks.Count(mark => mark.HasValue);

    private bool SourcesCurrent()
    {
        if (!_ownsSources()) return false;
        for (int index = 0; index < _curves.Length; index++)
            if (!_identities[index].IsCurrent || _curves[index].IsDestroyed ||
                _curves[index].IsDestroyQueued || !ReferenceEquals(_curves[index].Keyframes, _tracks[index]))
                return false;
        return true;
    }

    internal bool Mark(int curveIndex, FloatKeyframe key)
    {
        ulong intent = IntentRevision;
        if ((uint)curveIndex >= (uint)Count || !SourcesCurrent() || intent != IntentRevision ||
            !ReferenceEquals(key.OwningTrack, _tracks[curveIndex]) ||
            !float.IsFinite(key.Second) || key.Second < 0 || key.AuthoredFrameIndex < -1 ||
            !key.TryCaptureAuthoredState(out var payload))
            return false;
        _marks[curveIndex] = (key, key.Second, key.AuthoredFrameIndex, payload,
            _tracks[curveIndex].ExactEditGeneration);
        IntentRevision++;
        return true;
    }

    internal void Unmark(int curveIndex)
    {
        if ((uint)curveIndex >= (uint)Count) return;
        _marks[curveIndex] = null;
        IntentRevision++;
    }

    internal FloatKeyframe? GetMarkedKey(int curveIndex)
        => (uint)curveIndex < (uint)Count ? _marks[curveIndex]?.Key : null;

    internal void PruneInvalid()
    {
        ulong intent = IntentRevision;
        bool sourcesCurrent = SourcesCurrent();
        if (intent != IntentRevision) return;
        for (int index = 0; index < Count; index++)
            if (_marks[index] is { } mark && (!sourcesCurrent ||
                !ReferenceEquals(mark.Key.OwningTrack, _tracks[index]) ||
                mark.Generation != _tracks[index].ExactEditGeneration ||
                mark.Key.Second != mark.Second || mark.Key.AuthoredFrameIndex != mark.Frame ||
                !mark.Key.TryCaptureAuthoredState(out var payload) || payload != mark.Payload))
                Unmark(index);
    }

    internal bool TryCapture(out FloatKeyframe[] targets, out ulong intent)
    {
        intent = IntentRevision;
        targets = [];
        PruneInvalid();
        if (intent != IntentRevision || !SourcesCurrent() || intent != IntentRevision || MarkedCount != Count) return false;
        targets = _marks.Select(mark => mark!.Value.Key).ToArray();
        return true;
    }

    internal void ClearIfCurrent(ulong intent)
    {
        if (IntentRevision != intent) return;
        Array.Clear(_marks);
        IntentRevision++;
    }
}
