using XREngine.Data.Core;

namespace XREngine.Components.Animation;

/// <summary>The selected measurement in use when calibration was committed, excluding irrelevant inactive-mode values.</summary>
public readonly record struct VrBodyMeasurementKey(EVrBodyMeasurementMode Mode, float ValueMeters, float StandingHeightToEyeHeightRatio)
{
    public static bool TryFromSettings(UserSettings? settings, out VrBodyMeasurementKey key)
    {
        key = default;
        if (settings is null || !VrBodyScaleCalculation.TryCompute(settings, new AvatarBodyMeasurements(1, 1, false, null), out _, out _))
            return false;
        bool height = settings.BodyMeasurementMode == EVrBodyMeasurementMode.Height;
        key = new(settings.BodyMeasurementMode, (height ? settings.PlayerHeight : settings.PlayerArmSpan)!.Value,
            height ? settings.StandingHeightToEyeHeightRatio : 0.0f);
        return true;
    }

    internal bool IsValid => Mode switch
    {
        EVrBodyMeasurementMode.Height => float.IsFinite(ValueMeters) && ValueMeters is >= 0.5f and <= 2.75f &&
            float.IsFinite(StandingHeightToEyeHeightRatio) && StandingHeightToEyeHeightRatio is >= 0.8f and <= 1.0f,
        EVrBodyMeasurementMode.ArmSpan => float.IsFinite(ValueMeters) && ValueMeters is >= 0.5f and <= 3.5f && StandingHeightToEyeHeightRatio == 0.0f,
        _ => false,
    };
}
