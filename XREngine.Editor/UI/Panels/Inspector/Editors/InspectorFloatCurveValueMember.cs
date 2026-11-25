using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Retains one exact authored handle state and all authored track peers.</summary>
internal sealed class InspectorFloatCurveValueMember : IInspectorFloatCurveEditMember
{
    private readonly InspectorFloatCurveSource _source;
    private readonly Keyframe[] _order;
    private readonly Func<bool>?[] _peerPayloads;
    private readonly float[] _seconds;
    private readonly int[] _frames;
    private readonly float _curveLength;
    private readonly float _trackLength;
    private readonly float _defaultValue;
    private readonly float _second;
    private readonly int _frame;
    private readonly VectorKeyframeAuthoredState<float> _before;
    private readonly VectorKeyframeAuthoredState<float> _after;

    private InspectorFloatCurveValueMember(InspectorFloatCurveSource source,
        Keyframe[] order, Func<bool>?[] peerPayloads, float[] seconds,
        int[] frames, FloatKeyframe key, VectorKeyframeAuthoredState<float> before,
        VectorKeyframeAuthoredState<float> after)
    {
        _source = source;
        _order = order;
        _peerPayloads = peerPayloads;
        _seconds = seconds;
        _frames = frames;
        Key = key;
        _before = before;
        _after = after;
        _second = key.Second;
        _frame = key.AuthoredFrameIndex;
        _curveLength = source.Curve.LengthInSeconds;
        _trackLength = source.Track.LengthInSeconds;
        _defaultValue = source.Curve.DefaultValue;
    }

    internal FloatKeyframe Key { get; }
    public bool HasChange => _before != _after;
    internal IReadOnlyList<Keyframe> ExistingKeys => _order;
    public ulong ExactGeneration => _source.Track.ExactEditGeneration;
    public ulong NotificationRevision => _source.Track.ChangedNotificationRevision;
    public bool CanNotify => _source.Track.CanAdvanceChangedNotificationRevision;
    public ulong ExactGenerationDelta => 0;
    public bool CanReserveTransitions(int count) => !HasChange ||
        _source.Track.CanReserveChangedNotifications(count);

    internal static bool TryCapture(InspectorFloatCurveSource source,
        FloatKeyframe key, Func<FloatKeyframe, bool> stage, int remainingKeys,
        out InspectorFloatCurveValueMember? member)
    {
        member = null;
        if (!source.IsCurrent() || !source.Track.CanAdvanceChangedNotificationRevision ||
            !ReferenceEquals(key.OwningTrack, source.Track) ||
            !float.IsFinite(source.Curve.LengthInSeconds) ||
            !float.IsFinite(source.Track.LengthInSeconds) ||
            source.Track.Count > remainingKeys ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } order ||
            order.Length > remainingKeys ||
            !key.TryCaptureAuthoredState(out var before))
            return false;

        if (!InspectorFloatCurveActions.TryStageAuthoredState(before, stage, out var after) ||
            !Finite(before))
            return false;

        Func<bool>?[] payloads = new Func<bool>?[order.Length];
        float[] seconds = new float[order.Length];
        int[] frames = new int[order.Length];
        bool found = false;
        float previousSecond = float.NegativeInfinity;
        for (int index = 0; index < order.Length; index++)
        {
            Keyframe existing = order[index];
            if (!float.IsFinite(existing.Second) || existing.Second < 0 ||
                existing.Second < previousSecond ||
                existing.AuthoredFrameIndex < -1)
                return false;
            if (ReferenceEquals(existing, key)) found = true;
            else if (AnimationClipAuthoringActions.CaptureKeyPayload(existing) is { } payload)
                payloads[index] = payload;
            else return false;
            seconds[index] = existing.Second;
            frames[index] = existing.AuthoredFrameIndex;
            previousSecond = existing.Second;
        }
        if (!found) return false;
        member = new(source, order, payloads, seconds, frames, key, before, after);
        return member.CanReserveTransitions(2) && member.Matches(after: false);
    }

    private static bool Finite(VectorKeyframeAuthoredState<float> state)
        => float.IsFinite(state.InValue) && float.IsFinite(state.OutValue) &&
            float.IsFinite(state.InTangent) && float.IsFinite(state.OutTangent);

    public bool Matches(bool after)
    {
        if (!_source.IsCurrent() ||
            _source.Curve.LengthInSeconds != _curveLength ||
            _source.Track.LengthInSeconds != _trackLength ||
            !_source.Curve.DefaultValue.Equals(_defaultValue) ||
            !ReferenceEquals(Key.OwningTrack, _source.Track) ||
            Key.Second != _second || Key.AuthoredFrameIndex != _frame ||
            !AnimationClipAuthoringActions.MatchesOrder(_source.Track, _order) ||
            !Key.TryCaptureAuthoredState(out var state) ||
            state != (after ? _after : _before))
            return false;
        for (int index = 0; index < _order.Length; index++)
            if (_order[index].Second != _seconds[index] ||
                _order[index].AuthoredFrameIndex != _frames[index] ||
                (_peerPayloads[index] is { } payload && !payload()))
                return false;
        return true;
    }

    public void Transition(bool forward, ulong expectedExactGeneration,
        ulong expectedNotificationRevision)
    {
        if (!Matches(!forward) || ExactGeneration != expectedExactGeneration ||
            NotificationRevision != expectedNotificationRevision ||
            (HasChange && !_source.Track.CanReserveChangedNotifications(1)) ||
            (!HasChange && !CanNotify))
            throw new InvalidOperationException("A selected animation value changed before edit.");
        if (!HasChange) return;
        if (!Key.TryApplyAuthoredState(forward ? _before : _after,
                forward ? _after : _before, _source.Track))
            throw new InvalidOperationException("A selected animation value changed before exact commit.");
        if (ExactGeneration != expectedExactGeneration ||
            NotificationRevision != expectedNotificationRevision + 1 || !CanNotify)
            throw new InvalidOperationException("A selected animation track changed during notification.");
    }
}
