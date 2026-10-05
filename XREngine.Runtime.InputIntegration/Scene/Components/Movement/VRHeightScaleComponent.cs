using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Input;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Components;

/// <summary>The sole VR avatar-scale writer; consumes explicit player measurements, never headset height.</summary>
public class VRHeightScaleComponent : HeightScaleBaseComponent
{
    private UserSettings? _playerSettings;
    private UserSettings? _observedSettings;
    private float _appliedBodyScale = 1.0f;
    private float _measuredModelEyeHeight;
    private string _measurementNotice = VrBodyScaleCalculation.UnsetNotice;
    private int _measurementsDirty;

    /// <summary>Optional per-player override; otherwise use the host's persisted user settings asset.</summary>
    [YamlIgnore, MemoryPackIgnore]
    public UserSettings? PlayerSettings
    {
        get => _playerSettings ?? RuntimeVrStateServices.PlayerSettings;
        set
        {
            if (SetField(ref _playerSettings, value))
                UpdateHeightScale();
        }
    }

    [YamlIgnore, MemoryPackIgnore]
    public float AppliedBodyScale => _appliedBodyScale;
    [YamlIgnore, MemoryPackIgnore]
    public string MeasurementNotice => _measurementNotice;
    protected override float ModelToRealWorldHeightRatio => _appliedBodyScale;
    protected override float ModelHeightMeters => _measuredModelEyeHeight;

    public override void ApplyMeasuredHeight(float modelHeight)
    {
        if (!float.IsFinite(modelHeight) || modelHeight <= 0.0001f)
            return;
        SetField(ref _measuredModelEyeHeight, modelHeight, nameof(ModelHeightMeters));
        UpdateHeightScale();
    }

    public override bool TryApplyPlayerMeasurements(out string notice)
    {
        ObserveSettings(PlayerSettings);
        IHumanoidHeightReference? humanoid = GetHumanoid();
        if (humanoid is null || !humanoid.TryGetCanonicalBodyMeasurements(EyeOffsetFromHead, out AvatarBodyMeasurements measurements))
        {
            notice = "The avatar has no valid canonical body measurement; check its skeleton and root-floor origin.";
            SetField(ref _measurementNotice, notice, nameof(MeasurementNotice));
            return false;
        }
        if (!VrBodyScaleCalculation.TryCompute(PlayerSettings, measurements, out float scale, out notice))
        {
            SetField(ref _measurementNotice, notice, nameof(MeasurementNotice));
            return false;
        }
        SetField(ref _measuredModelEyeHeight, measurements.EyeHeight, "MeasuredModelEyeHeight");
        SetField(ref _appliedBodyScale, scale, nameof(AppliedBodyScale));
        SetField(ref _measurementNotice, notice, nameof(MeasurementNotice));
        base.UpdateHeightScale();
        return true;
    }

    public override string? GetCaptureMeasurementWarning(float trackedEyeHeightMeters)
        => VrBodyScaleCalculation.GetCaptureHeightWarning(PlayerSettings, trackedEyeHeightMeters);

    protected override void UpdateHeightScale()
        => TryApplyPlayerMeasurements(out _);

    private void ObserveSettings(UserSettings? settings)
    {
        if (ReferenceEquals(_observedSettings, settings))
            return;
        if (_observedSettings is not null)
            _observedSettings.PropertyChanged -= OnMeasurementChanged;
        SetField(ref _observedSettings, settings, "ObservedPlayerSettings");
        if (_observedSettings is not null)
            _observedSettings.PropertyChanged += OnMeasurementChanged;
    }

    private void OnMeasurementChanged(object? sender, IXRPropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UserSettings.BodyMeasurementMode) or nameof(UserSettings.PlayerHeight) or
            nameof(UserSettings.PlayerArmSpan) or nameof(UserSettings.StandingHeightToEyeHeightRatio))
            Interlocked.Exchange(ref _measurementsDirty, 1);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyPendingMeasurements);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyPendingMeasurements);
        base.OnComponentDeactivated();
    }

    private void ApplyPendingMeasurements()
    {
        if (Interlocked.Exchange(ref _measurementsDirty, 0) != 0)
            UpdateHeightScale();
    }

    protected override void OnDestroying()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyPendingMeasurements);
        ObserveSettings(null);
        base.OnDestroying();
    }
}
