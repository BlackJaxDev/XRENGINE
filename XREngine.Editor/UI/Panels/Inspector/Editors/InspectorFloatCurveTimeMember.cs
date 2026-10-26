using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Retains an exact time move, key order, and complete peer payloads.</summary>
internal sealed class InspectorFloatCurveTimeMember : IInspectorFloatCurveEditMember
{
    private readonly InspectorFloatCurveSource _source;
    private readonly FloatKeyframe _key;
    private readonly Keyframe[] _before;
    private readonly Keyframe[] _after;
    private readonly Func<bool>[] _payloads;
    private readonly float[] _seconds;
    private readonly int[] _frames;
    private readonly float _second;
    private readonly float _desiredSecond;
    private readonly int _frame;
    private readonly float _curveLength;
    private readonly float _trackLength;
    private readonly float _defaultValue;

    private InspectorFloatCurveTimeMember(InspectorFloatCurveSource source, FloatKeyframe key,
        Keyframe[] before, Func<bool>[] payloads, float[] seconds, int[] frames, float second)
    {
        _source = source;
        _key = key;
        _before = before;
        _payloads = payloads;
        _seconds = seconds;
        _frames = frames;
        _second = key.Second;
        _frame = key.AuthoredFrameIndex;
        _desiredSecond = second;
        _after = HasChange ? AnimationClipAuthoringActions.OrderWithMovedKey(before, key, second) : before;
        _curveLength = source.Curve.LengthInSeconds;
        _trackLength = source.Track.LengthInSeconds;
        _defaultValue = source.Curve.DefaultValue;
    }

    public bool HasChange => _second != _desiredSecond;
    public ulong ExactGeneration => _source.Track.ExactEditGeneration;
    public ulong ExactGenerationDelta => HasChange ? 1UL : 0UL;
    public ulong NotificationRevision => _source.Track.ChangedNotificationRevision;
    public bool CanNotify => _source.Track.CanAdvanceChangedNotificationRevision;
    public bool CanReserveTransitions(int count) => count >= 0 && (!HasChange ||
        ExactGeneration <= ulong.MaxValue - (ulong)count &&
        _source.Track.CanReserveChangedNotifications(count));
    internal IReadOnlyList<Keyframe> ExistingKeys => _before;

    internal static bool TryCapture(InspectorFloatCurveSource source, FloatKeyframe key,
        float second, int remainingKeys, out InspectorFloatCurveTimeMember? member)
    {
        member = null;
        if (!source.IsCurrent() || !source.Track.CanAdvanceChangedNotificationRevision ||
            !float.IsFinite(second) || second < 0 ||
            !float.IsFinite(source.Curve.LengthInSeconds) ||
            !float.IsFinite(source.Track.LengthInSeconds) ||
            second > source.Curve.LengthInSeconds || second > source.Track.LengthInSeconds ||
            !ReferenceEquals(key.OwningTrack, source.Track) || source.Track.Count > remainingKeys ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } order ||
            order.Length > remainingKeys)
            return false;
        Func<bool>[] payloads = new Func<bool>[order.Length];
        float[] seconds = new float[order.Length];
        int[] frames = new int[order.Length];
        bool found = false;
        float previous = float.NegativeInfinity;
        for (int index = 0; index < order.Length; index++)
        {
            Keyframe existing = order[index];
            if (!float.IsFinite(existing.Second) || existing.Second < 0 || existing.Second < previous ||
                existing.AuthoredFrameIndex < -1 ||
                AnimationClipAuthoringActions.CaptureKeyPayload(existing) is not { } payload)
                return false;
            found |= ReferenceEquals(existing, key);
            seconds[index] = previous = existing.Second;
            frames[index] = existing.AuthoredFrameIndex;
            payloads[index] = payload;
        }
        if (!found) return false;
        member = new(source, key, order, payloads, seconds, frames, second);
        return member.CanReserveTransitions(2) && member.Matches(false);
    }

    public bool Matches(bool after)
    {
        if (!_source.IsCurrent() || _source.Curve.LengthInSeconds != _curveLength ||
            _source.Track.LengthInSeconds != _trackLength ||
            !_source.Curve.DefaultValue.Equals(_defaultValue) ||
            !ReferenceEquals(_key.OwningTrack, _source.Track) ||
            _key.Second != (after ? _desiredSecond : _second) ||
            _key.AuthoredFrameIndex != (after && HasChange ? -1 : _frame) ||
            !AnimationClipAuthoringActions.MatchesOrder(_source.Track, after ? _after : _before))
            return false;
        for (int index = 0; index < _before.Length; index++)
            if (!_payloads[index]() || !ReferenceEquals(_before[index], _key) &&
                (_before[index].Second != _seconds[index] || _before[index].AuthoredFrameIndex != _frames[index]))
                return false;
        return true;
    }

    public void Transition(bool forward, ulong expectedExactGeneration, ulong expectedNotificationRevision)
    {
        if (!Matches(!forward) || ExactGeneration != expectedExactGeneration ||
            NotificationRevision != expectedNotificationRevision || !CanNotify || !CanReserveTransitions(1))
            throw new InvalidOperationException("A selected animation key changed before move.");
        if (!HasChange) return;
        if (!_source.Track.TryApplyExactKeyEdit(forward ? _before : _after, forward ? _after : _before,
                _key, forward ? _second : _desiredSecond, forward ? _frame : -1,
                forward ? _desiredSecond : _second, forward ? -1 : _frame))
            throw new InvalidOperationException("A selected animation key changed before exact move.");
        if (ExactGeneration != expectedExactGeneration + 1 ||
            NotificationRevision != expectedNotificationRevision + 1 || !CanNotify)
            throw new InvalidOperationException("A selected animation track changed during move notification.");
    }
}
