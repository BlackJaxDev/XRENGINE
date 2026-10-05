using System.ComponentModel;

namespace XREngine;

public partial class UserSettings
{
    private float _calibrationHeadTiltTolerance = 10.0f;
    private float _trackerBindingCutoff = 0.3f;
    private float _trackingLossHoldSeconds = 0.15f;
    private float _trackingSourceBlendSeconds = 0.25f;

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

    private static void ValidateRange(float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value must be between {minimum} and {maximum}.");
    }
}
