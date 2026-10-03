using System.ComponentModel;

namespace XREngine;

public partial class UserSettings
{
    private EBodyMeasurementMode _bodyMeasurementMode;
    private float _playerHeight;
    private float _playerArmSpan;
    private float _calibrationHeadTiltTolerance = 10.0f;
    private float _trackerBindingCutoff = 0.3f;
    private float _trackingLossHoldSeconds = 0.15f;
    private float _trackingSourceBlendSeconds = 0.25f;

    [Category("VR Body")]
    public EBodyMeasurementMode BodyMeasurementMode
    {
        get => _bodyMeasurementMode;
        set { if (SetField(ref _bodyMeasurementMode, value)) MarkDirty(); }
    }

    [Category("VR Body"), Description("Standing height in meters. Zero means not set. Eye height is estimated as 93.6% of standing height.")]
    public float PlayerHeight
    {
        get => _playerHeight;
        set { ValidateMeasurement(value); if (SetField(ref _playerHeight, value)) MarkDirty(); }
    }

    [Category("VR Body"), Description("Fingertip-to-fingertip arm span in meters. Zero means not set.")]
    public float PlayerArmSpan
    {
        get => _playerArmSpan;
        set { ValidateMeasurement(value); if (SetField(ref _playerArmSpan, value)) MarkDirty(); }
    }

    [Category("VR Body")]
    public float CalibrationHeadTiltTolerance
    {
        get => _calibrationHeadTiltTolerance;
        set { ValidateRange(value, 1.0f, 45.0f); if (SetField(ref _calibrationHeadTiltTolerance, value)) MarkDirty(); }
    }

    [Category("VR Body"), Description("Tracker binding radius at unit avatar scale, in meters.")]
    public float TrackerBindingCutoff
    {
        get => _trackerBindingCutoff;
        set { ValidateRange(value, 0.05f, 1.0f); if (SetField(ref _trackerBindingCutoff, value)) MarkDirty(); }
    }

    [Category("VR Body")]
    public float TrackingLossHoldSeconds
    {
        get => _trackingLossHoldSeconds;
        set { ValidateRange(value, 0.0f, 2.0f); if (SetField(ref _trackingLossHoldSeconds, value)) MarkDirty(); }
    }

    [Category("VR Body")]
    public float TrackingSourceBlendSeconds
    {
        get => _trackingSourceBlendSeconds;
        set { ValidateRange(value, 0.01f, 2.0f); if (SetField(ref _trackingSourceBlendSeconds, value)) MarkDirty(); }
    }

    private static void ValidateMeasurement(float value)
    {
        if (value != 0.0f) ValidateRange(value, 0.4f, 3.0f);
    }

    private static void ValidateRange(float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {minimum} and {maximum}.");
    }
}
