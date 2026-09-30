using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine;

public partial class UserSettings
{
    private EVrBodyMeasurementMode _bodyMeasurementMode;
    private float? _playerHeight;
    private float? _playerArmSpan;
    private float _standingHeightToEyeHeightRatio = 0.936f;
    private float _heightCalibrationWarningToleranceMeters = 0.12f;

    [Category("VR Body Measurements")]
    public EVrBodyMeasurementMode BodyMeasurementMode
    {
        get => _bodyMeasurementMode;
        set => SetField(ref _bodyMeasurementMode, value);
    }

    [Category("VR Body Measurements")]
    [Description("Standing height in meters, not eye height. Leave unset until entered by the player.")]
    public float? PlayerHeight
    {
        get => _playerHeight;
        set => SetField(ref _playerHeight, value);
    }

    [Category("VR Body Measurements")]
    [Description("Fingertip-to-fingertip arm span in meters with both arms stretched horizontally.")]
    public float? PlayerArmSpan
    {
        get => _playerArmSpan;
        set => SetField(ref _playerArmSpan, value);
    }

    [Category("VR Body Measurements")]
    [Description("Estimated eye height / standing height. The default 0.936 is a configurable approximation, not a measured individual proportion.")]
    public float StandingHeightToEyeHeightRatio
    {
        get => _standingHeightToEyeHeightRatio;
        set => SetField(ref _standingHeightToEyeHeightRatio, value);
    }

    [Category("VR Body Measurements")]
    [Description("Warn at capture when tracked eye height differs from the entered height-derived eye height by this many meters. Never changes the entered value.")]
    public float HeightCalibrationWarningToleranceMeters
    {
        get => _heightCalibrationWarningToleranceMeters;
        set => SetField(ref _heightCalibrationWarningToleranceMeters, value);
    }
}
