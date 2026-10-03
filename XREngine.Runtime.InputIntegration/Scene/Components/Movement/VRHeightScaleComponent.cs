using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Core;
using XREngine.Input;
using XREngine.Scene;

namespace XREngine.Components;

/// <summary>Owns avatar scale using an explicitly entered player measurement in metric tracking space.</summary>
public class VRHeightScaleComponent : HeightScaleBaseComponent
{
    public const float StandingHeightToEyeHeight = 0.936f;
    private UserSettings? _playerSettings;
    private float _modelEyeHeight;
    private float _modelArmSpan;
    private float _ratio = 1.0f;
    private float _lastMeasurement = -1.0f;
    private EBodyMeasurementMode _lastMode;
    private bool _armSpanUsesEstimatedHands;
    private bool _hasMeasuredAvatarEyeHeight;

    public UserSettings? PlayerSettings
    {
        get => _playerSettings;
        set { if (SetField(ref _playerSettings, value)) RefreshMeasurement(); }
    }

    public bool HasPlayerMeasurement => _hasMeasuredAvatarEyeHeight && PlayerSettings is { } settings &&
        float.IsFinite(settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? settings.PlayerHeight : settings.PlayerArmSpan) &&
        (settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? settings.PlayerHeight : settings.PlayerArmSpan) > 0.0f &&
        (settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? _modelEyeHeight : _modelArmSpan) > 0.01f;
    public string MeasurementNotice { get; private set; } = "Enter your height or arm span in VR Body settings.";
    public float AvatarScale => _ratio;
    protected override float ModelToRealWorldHeightRatio => _ratio;
    protected override float ModelHeightMeters => _modelEyeHeight;

    protected override void OnComponentActivated()
    {
        MeasureAvatarHeight();
        RefreshMeasurement();
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, RefreshMeasurement);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, RefreshMeasurement);
        base.OnComponentDeactivated();
    }

    public override void ApplyMeasuredHeight(float modelHeight)
    {
        if (!float.IsFinite(modelHeight) || modelHeight <= 0.01f)
            return;
        SetField(ref _modelEyeHeight, modelHeight);
        _hasMeasuredAvatarEyeHeight = true;
        MeasureArmSpan();
        _lastMeasurement = -1.0f;
        RefreshMeasurement();
    }

    /// <summary>Uses anatomical chain lengths, so an A-pose cannot shorten the measured span.</summary>
    public void MeasureArmSpan()
    {
        SetField(ref _modelArmSpan, 0.0f);
        _armSpanUsesEstimatedHands = false;
        _lastMeasurement = -1.0f;
        if ((HumanoidComponent as IHumanoidVrCalibrationRig ?? GetHumanoid() as IHumanoidVrCalibrationRig) is not { } rig)
            return;
        SceneNode? left = rig.LeftShoulderNode, right = rig.RightShoulderNode;
        if (left is null || right is null || rig.LeftElbowNode is null || rig.RightElbowNode is null ||
            rig.LeftHandNode is null || rig.RightHandNode is null)
            return;
        Matrix4x4 rootInverseBind = rig.RootTransform.InverseBindMatrix;
        float span = Distance(left, right, rootInverseBind)
            + UpperArmLength(left, rig.LeftUpperArmNode, rig.LeftElbowNode, rootInverseBind)
            + Distance(rig.LeftElbowNode, rig.LeftHandNode, rootInverseBind)
            + UpperArmLength(right, rig.RightUpperArmNode, rig.RightElbowNode, rootInverseBind)
            + Distance(rig.RightElbowNode, rig.RightHandNode, rootInverseBind);
        // A distal joint is not the fingertip. Measure a mapped endpoint when available;
        // estimate only the hand whose endpoint is absent.
        span += rig.LeftMiddleFingertipNode is { } leftTip
            ? Distance(rig.LeftHandNode, leftTip, rootInverseBind)
            : _modelEyeHeight * 0.108f;
        span += rig.RightMiddleFingertipNode is { } rightTip
            ? Distance(rig.RightHandNode, rightTip, rootInverseBind)
            : _modelEyeHeight * 0.108f;
        if (!float.IsFinite(span) || span <= 0.01f)
            return;
        SetField(ref _modelArmSpan, span);
        _armSpanUsesEstimatedHands = rig.LeftMiddleFingertipNode is null || rig.RightMiddleFingertipNode is null;
        _lastMeasurement = -1.0f;
    }

    private static float Distance(SceneNode a, SceneNode b, in Matrix4x4 rootInverseBind)
        => Vector3.Distance(
            Vector3.Transform(a.Transform.BindMatrix.Translation, rootInverseBind),
            Vector3.Transform(b.Transform.BindMatrix.Translation, rootInverseBind));

    private static float UpperArmLength(SceneNode shoulder, SceneNode? upperArm, SceneNode elbow, in Matrix4x4 rootInverseBind)
        => upperArm is null
            ? Distance(shoulder, elbow, rootInverseBind)
            : Distance(shoulder, upperArm, rootInverseBind) + Distance(upperArm, elbow, rootInverseBind);

    public void RefreshMeasurement()
    {
        if (PlayerSettings is not { } settings)
            return;
        float measurement = settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? settings.PlayerHeight : settings.PlayerArmSpan;
        if (measurement == _lastMeasurement && settings.BodyMeasurementMode == _lastMode)
            return;
        _lastMeasurement = measurement;
        _lastMode = settings.BodyMeasurementMode;
        if (!float.IsFinite(measurement) || measurement <= 0.0f)
        {
            MeasurementNotice = "Enter your height or arm span in VR Body settings.";
            return;
        }
        if (!_hasMeasuredAvatarEyeHeight)
        {
            MeasurementNotice = "Avatar eye height is unavailable; check its bone mapping.";
            return;
        }
        float denominator = settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? _modelEyeHeight : _modelArmSpan;
        float numerator = settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? measurement * StandingHeightToEyeHeight : measurement;
        if (!float.IsFinite(denominator) || denominator <= 0.01f)
        {
            MeasurementNotice = "Avatar body measurements are unavailable; check its bone mapping.";
            return;
        }
        float ratio = numerator / denominator;
        if (!float.IsFinite(ratio) || ratio <= 0.0f)
        {
            MeasurementNotice = "Body measurements must be finite and positive.";
            return;
        }
        SetField(ref _ratio, ratio);
        RuntimeVrStateServices.ModelHeight = _modelEyeHeight;
        RuntimeVrStateServices.RealWorldHeight = _modelEyeHeight * ratio;
        UpdateHeightScale();
        MeasurementNotice = settings.BodyMeasurementMode == EBodyMeasurementMode.ArmSpan && _armSpanUsesEstimatedHands
            ? "Arm span uses estimated hand lengths because fingertip endpoints are not mapped."
            : "";
    }
}
