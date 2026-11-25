using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Retains one exact track insertion and its authored neighbors.</summary>
internal sealed class InspectorFloatCurveAddMember
{
    private readonly InspectorFloatCurveSource _source;
    private readonly Keyframe[] _before;
    private readonly Keyframe[] _after;
    private readonly Func<bool>[] _payloads;
    private readonly float[] _seconds;
    private readonly int[] _frames;
    private readonly Func<bool> _newPayload;
    private readonly float _second;
    private readonly float _curveLength;
    private readonly float _trackLength;
    private readonly float _defaultValue;

    private InspectorFloatCurveAddMember(InspectorFloatCurveSource source, Keyframe[] before,
        Keyframe[] after, Func<bool>[] payloads, float[] seconds, int[] frames,
        FloatKeyframe key, Func<bool> newPayload, float second)
    {
        _source = source;
        _before = before;
        _after = after;
        _payloads = payloads;
        _seconds = seconds;
        _frames = frames;
        Key = key;
        _newPayload = newPayload;
        _second = second;
        _curveLength = source.Curve.LengthInSeconds;
        _trackLength = source.Track.LengthInSeconds;
        _defaultValue = source.Curve.DefaultValue;
    }

    internal FloatKeyframe Key { get; }
    internal IReadOnlyList<Keyframe> ExistingKeys => _before;
    internal ulong ExactGeneration => _source.Track.ExactEditGeneration;

    internal static bool TryCapture(InspectorFloatCurveSource source, float second,
        int remainingKeys, out InspectorFloatCurveAddMember? member)
    {
        member = null;
        if (!source.IsCurrent() || !source.Track.CanAdvanceExactEditGeneration ||
            !float.IsFinite(source.Curve.LengthInSeconds) ||
            !float.IsFinite(source.Track.LengthInSeconds) ||
            second < 0 || second > source.Curve.LengthInSeconds ||
            second > source.Track.LengthInSeconds ||
            source.Track.Count > remainingKeys ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } before ||
            before.Length > remainingKeys)
            return false;
        Func<bool>[] payloads = new Func<bool>[before.Length];
        float[] seconds = new float[before.Length];
        int[] frames = new int[before.Length];
        for (int index = 0; index < before.Length; index++)
        {
            Keyframe existing = before[index];
            if (AnimationClipAuthoringActions.CaptureKeyPayload(existing) is not { } payload ||
                !float.IsFinite(existing.Second) || existing.Second < 0 ||
                existing.AuthoredFrameIndex < -1)
                return false;
            payloads[index] = payload;
            seconds[index] = existing.Second;
            frames[index] = existing.AuthoredFrameIndex;
        }
        FloatKeyframe key = new(second, source.Curve.DefaultValue, 0,
            EVectorInterpType.Linear);
        Func<bool>? newPayload = AnimationClipAuthoringActions.CaptureKeyPayload(key);
        if (newPayload is null || key.AuthoredFrameIndex != -1)
            return false;
        Keyframe[] after = AnimationClipAuthoringActions.OrderWithInsertedKey(before,
            key, second);
        member = new(source, before, after, payloads, seconds, frames,
            key, newPayload, second);
        return member.Matches(after: false);
    }

    internal bool Matches(bool after)
    {
        if (!_source.IsCurrent() ||
            _source.Curve.LengthInSeconds != _curveLength ||
            _source.Track.LengthInSeconds != _trackLength ||
            !_source.Curve.DefaultValue.Equals(_defaultValue) ||
            Key.Second != _second || Key.AuthoredFrameIndex != -1 ||
            !_newPayload() ||
            !AnimationClipAuthoringActions.MatchesOrder(_source.Track,
                after ? _after : _before) ||
            (after ? !ReferenceEquals(Key.OwningTrack, _source.Track) :
                Key.OwningTrack is not null || Key.Prev is not null || Key.Next is not null))
            return false;
        for (int index = 0; index < _before.Length; index++)
            if (_before[index].Second != _seconds[index] ||
                _before[index].AuthoredFrameIndex != _frames[index] ||
                !_payloads[index]())
                return false;
        return true;
    }

    internal void Transition(bool forward, ulong expectedGeneration)
    {
        if (!Matches(!forward) || !_source.Track.CanAdvanceExactEditGeneration ||
            ExactGeneration != expectedGeneration)
            throw new InvalidOperationException("A selected animation key changed before insertion.");
        if (!_source.Track.TryApplyExactKeyEdit(
                forward ? _before : _after, forward ? _after : _before,
                Key, _second, -1, _second, -1))
            throw new InvalidOperationException("A selected animation key changed before exact edit.");
        if (ExactGeneration != expectedGeneration + 1)
            throw new InvalidOperationException("A selected animation track changed during notification.");
    }
}
