using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine.Editor;

/// <summary>Owns exact authored timing for one built-in keyframed animation.</summary>
internal sealed class AnimationLengthEdit
{
    private readonly BasePropAnimKeyframed _animation;
    private readonly BaseKeyframeTrack _track;
    private readonly XRAsset _document;
    private readonly Guid _identity;
    private readonly Guid _documentIdentity;
    private readonly Keyframe[] _order;
    private readonly Func<bool>[] _payloads;
    private readonly float[] _beforeSeconds;
    private readonly float[] _afterSeconds;
    private readonly int[] _beforeFrames;
    private readonly int[] _afterFrames;
    private readonly float _beforeLength;
    private readonly float _beforeTrackLength;
    private readonly float _afterLength;
    private readonly int _beforeFrameCount;
    private readonly int _afterFrameCount;
    private readonly int _fps;
    private readonly int _cadenceFrames;
    private readonly int _cadenceFps;
    private readonly bool _isBaked;
    private readonly object? _bakedStore;
    private readonly int _bakedStoreCount;
    private readonly EAnimationValueCompressionAlgorithm _compression;
    private readonly EAnimationValueCompressionAlgorithm _encodedCompression;
    private readonly (bool Constrain, bool Lerp) _samplingFlags;
    private ulong _generation;

    private AnimationLengthEdit(BasePropAnimKeyframed animation, BaseKeyframeTrack track,
        Keyframe[] order, Func<bool>[] payloads, float seconds, bool stretch)
    {
        _animation = animation;
        _track = track;
        _document = animation.SourceAsset;
        _identity = animation.ID;
        _documentIdentity = _document.ID;
        _order = order;
        _payloads = payloads;
        _beforeLength = animation.LengthInSeconds;
        _beforeTrackLength = track.LengthInSeconds;
        _afterLength = seconds;
        _beforeFrameCount = animation.BakedFrameCount;
        _fps = animation.BakedFramesPerSecond;
        _afterFrameCount = checked((int)Math.Ceiling(seconds * _fps));
        _cadenceFrames = animation.AuthoredFrameCount;
        _cadenceFps = animation.AuthoredFramesPerSecond;
        _isBaked = animation.IsBaked;
        _bakedStore = animation.ExactBakedStore;
        _bakedStoreCount = animation.ExactBakedStoreCount;
        _compression = animation.BakedValueCompressionAlgorithm;
        _encodedCompression = animation.EncodedBakedValueCompressionAlgorithm;
        _samplingFlags = animation.ExactSamplingFlags;
        _generation = track.ExactEditGeneration;
        _beforeSeconds = new float[order.Length];
        _afterSeconds = new float[order.Length];
        _beforeFrames = new int[order.Length];
        _afterFrames = new int[order.Length];
        float ratio = stretch && _beforeTrackLength > 0 ? seconds / _beforeTrackLength : 1;
        for (int index = 0; index < order.Length; index++)
        {
            Keyframe key = order[index];
            float next = key.Second * ratio;
            if (!float.IsFinite(next) || next < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Stretched key time is not finite.");
            _beforeSeconds[index] = key.Second;
            _afterSeconds[index] = next;
            _beforeFrames[index] = key.AuthoredFrameIndex;
            _afterFrames[index] = MathF.Abs(next - key.Second) > 0.000001f
                ? -1 : key.AuthoredFrameIndex;
        }
    }

    public XRAsset Document => _document;
    public BasePropAnimKeyframed Animation => _animation;
    public BaseKeyframeTrack Track => _track;
    public ReadOnlySpan<Keyframe> Keys => _order;
    public bool HasChange => _beforeLength != _afterLength ||
        _beforeTrackLength != _afterLength || _beforeFrameCount != _afterFrameCount ||
        !_beforeSeconds.AsSpan().SequenceEqual(_afterSeconds) ||
        !_beforeFrames.AsSpan().SequenceEqual(_afterFrames);

    public static bool TryCapture(BasePropAnim animation, BaseKeyframeTrack track,
        Keyframe[] order, Func<bool>[] payloads, float seconds, bool stretch,
        out AnimationLengthEdit? edit, out string reason)
    {
        edit = null;
        reason = "Length editing requires a built-in keyframed animation and track.";
        Type? keyType = animation switch
        {
            PropAnimFloat when animation.GetType() == typeof(PropAnimFloat) && track.GetType() == typeof(KeyframeTrack<FloatKeyframe>) => typeof(FloatKeyframe),
            PropAnimVector2 when animation.GetType() == typeof(PropAnimVector2) && track.GetType() == typeof(KeyframeTrack<Vector2Keyframe>) => typeof(Vector2Keyframe),
            PropAnimVector3 when animation.GetType() == typeof(PropAnimVector3) && track.GetType() == typeof(KeyframeTrack<Vector3Keyframe>) => typeof(Vector3Keyframe),
            PropAnimVector4 when animation.GetType() == typeof(PropAnimVector4) && track.GetType() == typeof(KeyframeTrack<Vector4Keyframe>) => typeof(Vector4Keyframe),
            PropAnimQuaternion when animation.GetType() == typeof(PropAnimQuaternion) && track.GetType() == typeof(KeyframeTrack<QuaternionKeyframe>) => typeof(QuaternionKeyframe),
            PropAnimBool when animation.GetType() == typeof(PropAnimBool) && track.GetType() == typeof(KeyframeTrack<BoolKeyframe>) => typeof(BoolKeyframe),
            _ => null
        };
        if (keyType is null || animation is not BasePropAnimKeyframed keyframed)
            return false;
        reason = "The animation track is shared, busy, or has invalid timing.";
        if (!keyframed.CanBeginExactLengthEdit || !keyframed.HasExclusiveLengthRelay ||
            !float.IsFinite(keyframed.LengthInSeconds) || keyframed.LengthInSeconds < 0 ||
            !float.IsFinite(track.LengthInSeconds) || track.LengthInSeconds < 0 ||
            !float.IsFinite(seconds) || seconds < 0 || keyframed.BakedFramesPerSecond < 0 ||
            keyframed.BakedFrameCount < 0 || !track.CanAdvanceExactEditGeneration ||
            order.Length != payloads.Length || !track.HasExactOrder(order))
            return false;
        float previous = float.NegativeInfinity;
        foreach (Keyframe key in order)
        {
            if (key.GetType() != keyType || !float.IsFinite(key.Second) ||
                key.Second < 0 || key.Second < previous || key.AuthoredFrameIndex < -1)
                return false;
            previous = key.Second;
        }
        try { edit = new(keyframed, track, order, payloads, seconds, stretch); }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            reason = "The requested length exceeds the supported timing range.";
            return false;
        }
        if (!edit.Matches(after: false))
        {
            edit = null;
            return false;
        }
        reason = "The requested length would read beyond retained baked samples.";
        if (!edit.HasSafeBakedSampling(after: false) ||
            !edit.HasSafeBakedSampling(after: true))
        {
            edit = null;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public bool Matches(bool after)
    {
        BaseKeyframeTrack? currentTrack = _animation switch
        {
            PropAnimFloat value => value.Keyframes,
            PropAnimVector2 value => value.Keyframes,
            PropAnimVector3 value => value.Keyframes,
            PropAnimVector4 value => value.Keyframes,
            PropAnimQuaternion value => value.Keyframes,
            PropAnimBool value => value.Keyframes,
            _ => null
        };
        if (_animation.IsDestroyed || _animation.IsDestroyQueued ||
            _document.IsDestroyed || _document.IsDestroyQueued ||
            _animation.ID != _identity || _document.ID != _documentIdentity ||
            !ReferenceEquals(_animation.SourceAsset, _document) ||
            !ReferenceEquals(currentTrack, _track) ||
            _animation.IsBaked != _isBaked ||
            _animation.AuthoredFrameCount != _cadenceFrames ||
            _animation.AuthoredFramesPerSecond != _cadenceFps ||
            (_isBaked &&
                (!ReferenceEquals(_animation.ExactBakedStore, _bakedStore) ||
                 _animation.ExactBakedStoreCount != _bakedStoreCount ||
                 _animation.BakedValueCompressionAlgorithm != _compression ||
                 _animation.EncodedBakedValueCompressionAlgorithm != _encodedCompression ||
                 _animation.ExactSamplingFlags != _samplingFlags)) ||
            _animation.BakedFramesPerSecond != _fps || !_animation.HasExclusiveLengthRelay ||
            _animation.LengthInSeconds != (after ? _afterLength : _beforeLength) ||
            _animation.BakedFrameCount != (after ? _afterFrameCount : _beforeFrameCount) ||
            _track.LengthInSeconds != (after ? _afterLength : _beforeTrackLength) ||
            !_track.HasExactOrder(_order))
            return false;
        float[] seconds = after ? _afterSeconds : _beforeSeconds;
        int[] frames = after ? _afterFrames : _beforeFrames;
        for (int index = 0; index < _order.Length; index++)
            if (_order[index].Second != seconds[index] ||
                _order[index].AuthoredFrameIndex != frames[index] || !_payloads[index]())
                return false;
        return true;
    }

    public bool MatchesCurrentCommit(bool after)
        => _track.ExactEditGeneration == _generation && Matches(after);

    public bool CanCommit(bool forward) => _animation.CanBeginExactLengthEdit &&
        _track.CanAdvanceExactEditGeneration && Matches(after: !forward) &&
        HasSafeBakedSampling(after: forward);

    private bool HasSafeBakedSampling(bool after)
    {
        if (!_isBaked)
            return true;
        if (_bakedStore is null)
            return false;
        if (!_samplingFlags.Lerp)
            return true;
        if (_fps == 0)
            return true;
        float length = after ? _afterLength : _beforeLength;
        float product = length * _fps;
        if (!float.IsFinite(product) || product < 0 || product >= 2147483648d)
            return false;
        int reachable = Math.Max(after ? _afterFrameCount : _beforeFrameCount,
            checked((int)Math.Ceiling(product)));
        return reachable == 0 || reachable <= _bakedStoreCount;
    }

    /// <summary>The caller validates the complete batch before any raw write.</summary>
    public void Commit(bool forward)
    {
        if (!_animation.TrySetExactLengthWithoutNotification(
                forward ? _beforeLength : _afterLength,
                forward ? _beforeFrameCount : _afterFrameCount,
                forward ? _afterLength : _beforeLength,
                forward ? _afterFrameCount : _beforeFrameCount) ||
            !_track.TrySetExactLength(forward ? _beforeTrackLength : _afterLength,
                forward ? _afterLength : _beforeTrackLength, _order,
                forward ? _beforeSeconds : _afterSeconds,
                forward ? _beforeFrames : _afterFrames,
                forward ? _afterSeconds : _beforeSeconds,
                forward ? _afterFrames : _beforeFrames))
            throw new InvalidOperationException("Animation timing changed before the raw commit.");
        _generation = _track.ExactEditGeneration;
    }

    public void Publish(bool forward, Action validateState)
        => _animation.PublishExactLengthChange(
            forward ? _beforeTrackLength : _afterLength, validateState);
}
