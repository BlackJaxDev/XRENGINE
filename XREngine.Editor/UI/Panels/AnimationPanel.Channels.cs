using System.Globalization;
using System.Numerics;
using XREngine.Animation;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

public partial class AnimationPanel
{
    private void CycleValueChannel()
    {
        int count = _selectedKey is null ? 0 : AnimationKeyChannels.Count(_selectedKey.ValueType);
        _valueChannel = count <= 1 || _valueChannel >= count - 1 ? -1 : _valueChannel + 1;
        RefreshValueInput();
    }

    private string ChannelLabel(Keyframe? key)
    {
        int count = key is null ? 0 : AnimationKeyChannels.Count(key.ValueType);
        return _valueChannel < 0 || _valueChannel >= count ? "all" :
            "XYZW"[_valueChannel].ToString();
    }

    private string FormatSelectedValue(Keyframe key)
    {
        object? value = GetKeyPart(key, KeyChannelPart.OutValue);
        return _valueChannel < 0 || value is null ||
            !AnimationKeyChannels.TryRead(value, _valueChannel, out float scalar)
            ? FormatKeyValue(key)
            : scalar.ToString("G9", CultureInfo.InvariantCulture);
    }

    private bool TryParseSelectedValue(string text, Keyframe key,
        KeyChannelPart part, out object? value)
    {
        value = null;
        if (_valueChannel < 0 || _valueChannel >= AnimationKeyChannels.Count(key.ValueType) ||
            AnimationKeyChannels.Count(key.ValueType) <= 1)
            return TryParseValue(text, key.ValueType, out value);
        if (!float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float scalar) || !float.IsFinite(scalar))
            return false;
        object? previous = GetKeyPart(key, part);
        return previous is not null && AnimationKeyChannels.TryReplace(previous,
            _valueChannel, scalar, out value);
    }

    private bool CanApplySelectedValue(ulong epoch, ulong sourceRevision,
        int channel, UITextInputComponent input, AnimationClip clip,
        AnimationMember member, Keyframe key)
        => !_destroying && IsActiveInHierarchy && !_valueInputRefreshPending &&
            !_refreshingValueInput &&
            _authoringEpoch == epoch && _sourceRevision == sourceRevision &&
            _valueChannel == channel && ReferenceEquals(_valueInput, input) &&
            ReferenceEquals(_displayedClip, clip) && ReferenceEquals(CurrentClip(), clip) &&
            ReferenceEquals(_selectedMember, member) && ReferenceEquals(_selectedKey, key);

    private static object? GetKeyPart(Keyframe key, KeyChannelPart part)
    {
        Type type = key.GetType();
        if (type != typeof(FloatKeyframe) && type != typeof(Vector2Keyframe) &&
            type != typeof(Vector3Keyframe) && type != typeof(Vector4Keyframe) &&
            type != typeof(QuaternionKeyframe) && type != typeof(BoolKeyframe))
            return null;
        return key switch
        {
        IPlanarKeyframe planar => part switch
        {
            KeyChannelPart.InValue => planar.InValue,
            KeyChannelPart.OutValue => planar.OutValue,
            KeyChannelPart.InTangent => planar.InTangent,
            _ => planar.OutTangent
        },
        QuaternionKeyframe quaternion => part switch
        {
            KeyChannelPart.InValue => quaternion.InValue,
            KeyChannelPart.OutValue => quaternion.OutValue,
            KeyChannelPart.InTangent => quaternion.InTangent,
            _ => quaternion.OutTangent
        },
        BoolKeyframe boolean when part == KeyChannelPart.OutValue => boolean.Value,
        _ => null
        };
    }
}
