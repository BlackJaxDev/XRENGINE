using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Retains one exact track removal and its authored neighbors.</summary>
internal sealed class InspectorFloatCurveRemoveMember
{
    private readonly InspectorFloatCurveSource _source;
    private readonly Keyframe[] _before;
    private readonly Keyframe[] _after;
    private readonly Func<bool>[] _payloads;
    private readonly float[] _seconds;
    private readonly int[] _frames;
    private readonly float _curveLength;
    private readonly float _trackLength;
    private readonly float _defaultValue;
    private readonly float _second;
    private readonly int _frame;

    private InspectorFloatCurveRemoveMember(InspectorFloatCurveSource source,
        Keyframe[] before, Keyframe[] after, Func<bool>[] payloads,
        float[] seconds, int[] frames, FloatKeyframe key)
    {
        _source = source;
        _before = before;
        _after = after;
        _payloads = payloads;
        _seconds = seconds;
        _frames = frames;
        Key = key;
        _second = key.Second;
        _frame = key.AuthoredFrameIndex;
        _curveLength = source.Curve.LengthInSeconds;
        _trackLength = source.Track.LengthInSeconds;
        _defaultValue = source.Curve.DefaultValue;
    }

    internal FloatKeyframe Key { get; }
    internal IReadOnlyList<Keyframe> ExistingKeys => _before;
    internal ulong ExactGeneration => _source.Track.ExactEditGeneration;

    internal static bool TryCapture(InspectorFloatCurveSource source,
        FloatKeyframe key, int remainingKeys,
        out InspectorFloatCurveRemoveMember? member)
    {
        member = null;
        if (!source.IsCurrent() || !source.Track.CanAdvanceExactEditGeneration ||
            !ReferenceEquals(key.OwningTrack, source.Track) ||
            !float.IsFinite(source.Curve.LengthInSeconds) ||
            !float.IsFinite(source.Track.LengthInSeconds) ||
            source.Track.Count > remainingKeys ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } before ||
            before.Length > remainingKeys)
            return false;

        Func<bool>[] payloads = new Func<bool>[before.Length];
        float[] seconds = new float[before.Length];
        int[] frames = new int[before.Length];
        int keyIndex = -1;
        float previousSecond = float.NegativeInfinity;
        for (int index = 0; index < before.Length; index++)
        {
            Keyframe existing = before[index];
            if (AnimationClipAuthoringActions.CaptureKeyPayload(existing) is not { } payload ||
                !float.IsFinite(existing.Second) || existing.Second < 0 ||
                existing.Second < previousSecond ||
                existing.AuthoredFrameIndex < -1)
                return false;
            if (ReferenceEquals(existing, key)) keyIndex = index;
            payloads[index] = payload;
            seconds[index] = existing.Second;
            frames[index] = existing.AuthoredFrameIndex;
            previousSecond = existing.Second;
        }
        if (keyIndex < 0) return false;
        Keyframe[] after = new Keyframe[before.Length - 1];
        if (keyIndex > 0) Array.Copy(before, after, keyIndex);
        if (keyIndex < after.Length)
            Array.Copy(before, keyIndex + 1, after, keyIndex, after.Length - keyIndex);
        member = new(source, before, after, payloads, seconds, frames, key);
        return member.Matches(after: false);
    }

    internal bool Matches(bool after)
    {
        if (!_source.IsCurrent() ||
            _source.Curve.LengthInSeconds != _curveLength ||
            _source.Track.LengthInSeconds != _trackLength ||
            !_source.Curve.DefaultValue.Equals(_defaultValue) ||
            Key.Second != _second || Key.AuthoredFrameIndex != _frame ||
            !AnimationClipAuthoringActions.MatchesOrder(_source.Track,
                after ? _after : _before) ||
            (after ? Key.OwningTrack is not null || Key.Prev is not null ||
                Key.Next is not null : !ReferenceEquals(Key.OwningTrack, _source.Track)))
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
            throw new InvalidOperationException("A selected animation key changed before removal.");
        if (!_source.Track.TryApplyExactKeyEdit(
                forward ? _before : _after, forward ? _after : _before,
                Key, _second, _frame, _second, _frame))
            throw new InvalidOperationException("A selected animation key changed before exact edit.");
        if (ExactGeneration != expectedGeneration + 1)
            throw new InvalidOperationException("A selected animation track changed during notification.");
    }
}
