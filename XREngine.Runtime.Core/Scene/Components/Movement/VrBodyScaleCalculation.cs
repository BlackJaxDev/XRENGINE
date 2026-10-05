using XREngine.Data.Core;

namespace XREngine.Components;

/// <summary>Validates player measurements without reading tracking poses or mutating any transform.</summary>
public static class VrBodyScaleCalculation
{
    public const string UnsetNotice = "Set your standing height or arm span in Player Settings before calibrating.";

    public static bool TryCompute(UserSettings? settings, AvatarBodyMeasurements avatar, out float scale, out string notice)
    {
        scale = 1.0f;
        notice = UnsetNotice;
        if (settings is null)
            return false;
        if (settings.BodyMeasurementMode is not EVrBodyMeasurementMode.Height and not EVrBodyMeasurementMode.ArmSpan)
        {
            notice = "Choose height or arm-span measurement mode.";
            return false;
        }

        float? entered = settings.BodyMeasurementMode == EVrBodyMeasurementMode.Height ? settings.PlayerHeight : settings.PlayerArmSpan;
        if (!entered.HasValue)
            return false;
        if (!float.IsFinite(entered.Value) || entered < 0.5f || entered > (settings.BodyMeasurementMode == EVrBodyMeasurementMode.Height ? 2.75f : 3.5f))
        {
            notice = "Enter a finite body measurement within the supported range (height 0.5–2.75 m; arm span 0.5–3.5 m).";
            return false;
        }
        float denominator;
        float requested;
        switch (settings.BodyMeasurementMode)
        {
            case EVrBodyMeasurementMode.Height:
                if (!float.IsFinite(settings.StandingHeightToEyeHeightRatio) || settings.StandingHeightToEyeHeightRatio is < 0.8f or > 1.0f)
                {
                    notice = "Standing-height-to-eye-height ratio must be finite and between 0.8 and 1.0.";
                    return false;
                }
                denominator = avatar.EyeHeight;
                requested = entered.Value * settings.StandingHeightToEyeHeightRatio;
                break;
            case EVrBodyMeasurementMode.ArmSpan:
                denominator = avatar.ArmSpan;
                requested = entered.Value;
                break;
            default:
                notice = "Choose height or arm-span measurement mode.";
                return false;
        }
        if (!float.IsFinite(denominator) || denominator <= 0.0001f || !float.IsFinite(avatar.EyeHeight) || avatar.EyeHeight <= 0.0001f)
        {
            notice = "The avatar has no valid canonical body measurement; check its skeleton and root-floor origin.";
            return false;
        }
        scale = requested / denominator;
        if (!float.IsFinite(scale) || scale <= 0.00001f || scale > 10000.0f)
        {
            scale = 1.0f;
            notice = "The requested avatar scale is outside the supported finite range.";
            return false;
        }
        notice = settings.BodyMeasurementMode == EVrBodyMeasurementMode.ArmSpan ? avatar.Notice ?? string.Empty : string.Empty;
        return true;
    }

    public static string? GetCaptureHeightWarning(UserSettings? settings, float trackedEyeHeightMeters)
    {
        if (settings?.BodyMeasurementMode != EVrBodyMeasurementMode.Height || settings.PlayerHeight is not float height)
            return null;
        if (!float.IsFinite(trackedEyeHeightMeters) || trackedEyeHeightMeters <= 0.0f)
            return "The headset eye height is unavailable; check the tracking floor origin.";
        float tolerance = settings.HeightCalibrationWarningToleranceMeters;
        if (!float.IsFinite(tolerance) || tolerance is < 0.01f or > 0.5f)
            return "Height warning tolerance must be finite and between 0.01 and 0.5 meters.";
        float expected = height * settings.StandingHeightToEyeHeightRatio;
        return !float.IsFinite(expected) || MathF.Abs(trackedEyeHeightMeters - expected) > tolerance
            ? "Headset eye height disagrees with your standing-height setting. Stand straight and check your measurement and tracking floor; your setting has not been changed."
            : null;
    }
}
