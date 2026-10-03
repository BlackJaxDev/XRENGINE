using XREngine.Input;

namespace XREngine.Components.VR;

public partial class VRPlayerCharacterComponent
{
    private const double MeasurementMenuHoldSeconds = 0.8;
    private const float MeasurementStepMeters = 0.01f;
    private bool _isEditingMeasurement;
    private bool _measurementOpenWasHeld;
    private bool _measurementHoldConsumed;
    private double _measurementHoldStarted;

    /// <summary>Whether the VR calibration controls currently edit the player's body measurement.</summary>
    public bool IsEditingMeasurement
    {
        get => _isEditingMeasurement;
        private set => SetField(ref _isEditingMeasurement, value);
    }

    private void UpdateMeasurementInput(int actions, double now)
    {
        if (!IsCalibrating)
        {
            ClearMeasurementEntry();
            return;
        }

        bool held = RuntimeVrInputServices.CalibrationOpenHeld;
        if (held && !_measurementOpenWasHeld)
        {
            _measurementHoldStarted = now;
            _measurementHoldConsumed = false;
        }
        _measurementOpenWasHeld = held;
        if (!held)
            _measurementHoldConsumed = false;
        else if (!_measurementHoldConsumed && now - _measurementHoldStarted >= MeasurementMenuHoldSeconds)
        {
            _measurementHoldConsumed = true;
            IsEditingMeasurement = !IsEditingMeasurement;
            RuntimeVrInputServices.CalibrationMeasurementEntryActive = IsEditingMeasurement;
            CalibrationMessage = IsEditingMeasurement
                ? PlayerSettings is { } currentSettings
                    ? FormatMeasurementMessage(currentSettings)
                    : "Player settings are unavailable; exit measurement editing and retry."
                : (HeightScaleComponent as VRHeightScaleComponent)?.HasPlayerMeasurement == true
                    ? "Measurement saved. Stand in the footprints, then pull both triggers to capture."
                    : (HeightScaleComponent as VRHeightScaleComponent)?.MeasurementNotice
                        ?? "Measurement is still unset. Hold the calibration button to enter it before capture.";
        }

        if (!IsEditingMeasurement || PlayerSettings is not { } settings)
            return;

        if ((actions & 128) != 0)
            settings.BodyMeasurementMode = settings.BodyMeasurementMode == EBodyMeasurementMode.Height
                ? EBodyMeasurementMode.ArmSpan
                : EBodyMeasurementMode.Height;
        if ((actions & 32) != 0)
            AdjustMeasurement(settings, -MeasurementStepMeters);
        if ((actions & 64) != 0)
            AdjustMeasurement(settings, MeasurementStepMeters);
        if ((actions & (32 | 64 | 128)) != 0)
            CalibrationMessage = FormatMeasurementMessage(settings);
    }

    private static void AdjustMeasurement(UserSettings settings, float delta)
    {
        bool height = settings.BodyMeasurementMode == EBodyMeasurementMode.Height;
        float current = height ? settings.PlayerHeight : settings.PlayerArmSpan;
        if (current == 0.0f)
            current = 1.70f;
        float updated = Math.Clamp(MathF.Round((current + delta) * 100.0f) / 100.0f, 0.4f, 3.0f);
        if (height)
            settings.PlayerHeight = updated;
        else
            settings.PlayerArmSpan = updated;
    }

    private static string FormatMeasurementMessage(UserSettings settings)
    {
        bool height = settings.BodyMeasurementMode == EBodyMeasurementMode.Height;
        float value = height ? settings.PlayerHeight : settings.PlayerArmSpan;
        string name = height ? "Standing height" : "Arm span";
        string amount = value == 0.0f ? "unset" : $"{value:F2} m";
        return $"{name}: {amount}. Left/right trigger adjusts by 1 cm; both switch mode. Hold the calibration button to finish.";
    }

    private void ClearMeasurementEntry()
    {
        IsEditingMeasurement = false;
        RuntimeVrInputServices.CalibrationMeasurementEntryActive = false;
        _measurementOpenWasHeld = false;
        _measurementHoldConsumed = false;
    }
}
