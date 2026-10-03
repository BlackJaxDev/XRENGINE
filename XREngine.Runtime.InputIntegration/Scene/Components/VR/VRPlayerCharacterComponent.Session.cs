using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Animation;
using XREngine.Data.Components.Scene;
using XREngine.Scene;

namespace XREngine.Components.VR;

public partial class VRPlayerCharacterComponent
{
    // The same avatar node survives an editor VR lease, while replacing the avatar
    // creates a different node even if its prefab and bind pose are identical.
    private static readonly ConditionalWeakTable<SceneNode, Dictionary<VrAvatarCalibrationKey, VrSessionCalibration>> SessionCalibrations = new();

    private VrAvatarCalibrationKey AvatarSignature()
        => new(_humanoid?.SceneNode.Prefab?.PrefabAssetId ?? Guid.Empty, _humanoid?.SceneNode.Name,
            _humanoid?.HeadNode?.Transform.BindMatrix ?? default,
            _humanoid?.HipsNode?.Transform.BindMatrix ?? default,
            _humanoid?.LeftHandNode?.Transform.BindMatrix ?? default,
            _humanoid?.RightHandNode?.Transform.BindMatrix ?? default,
            _humanoid?.LeftFootNode?.Transform.BindMatrix ?? default,
            _humanoid?.RightFootNode?.Transform.BindMatrix ?? default);

    private void SaveSessionCalibration()
    {
        if (!HasCalibration || _rig is null || _humanoid is null) return;
        var saved = new VrSessionCalibration
        {
            Mode = _calibratedMeasurementMode,
            Measurement = _calibratedMeasurement,
        };
        for (int i = 0; i < 11; i++)
        {
            if (!_rig.TryGetSlot((EHumanoidIKTarget)i, out var state)) continue;
            saved.Identities[i] = _boundIdentities[i];
            saved.Offsets[i] = state.DeviceToTargetOffset;
        }
        Dictionary<VrAvatarCalibrationKey, VrSessionCalibration> calibrations =
            SessionCalibrations.GetValue(_humanoid.SceneNode, static _ => new());
        calibrations.Clear();
        calibrations[AvatarSignature()] = saved;
    }

    private float MeasurementInUse() => PlayerSettings is not { } settings ? 0.0f :
        settings.BodyMeasurementMode == EBodyMeasurementMode.Height ? settings.PlayerHeight : settings.PlayerArmSpan;

    private void RestoreSessionCalibration()
    {
        if (_rig is null || _humanoid is null ||
            !SessionCalibrations.TryGetValue(_humanoid.SceneNode, out var calibrations) ||
            !calibrations.TryGetValue(AvatarSignature(), out var saved)) return;
        if (saved.Mode != PlayerSettings?.BodyMeasurementMode || saved.Measurement != MeasurementInUse())
        {
            CalibrationMessage = "Body measurements changed. Reopen calibration.";
            return;
        }
        if (!TryBuildRequest(false, out var request, out _)) return;
        VrCalibrationCapture head = request.Slots[(int)EHumanoidIKTarget.Head]!.Value;
        bool missing = false;
        for (int i = 0; i < 11; i++)
        {
            request.Offsets[i] = saved.Offsets[i];
            if (saved.Identities[i] is not { } identity) continue;
            VRTrackerTransform? device = FindTracker(identity);
            Matrix4x4 world = Matrix4x4.Identity;
            long snapshot = 0, sampleTime = 0;
            bool valid = device?.TryGetCurrentWorldPose(out world, out snapshot, out sampleTime) == true;
            if (valid && (snapshot != head.SnapshotId || sampleTime != head.SampleTime))
            {
                _headHandsInitialized = false;
                CalibrationMessage = "Waiting for coherent tracking before restoring calibration.";
                return;
            }
            missing |= !valid;
            request.Slots[i] = new VrCalibrationCapture(valid ? device : null, world, identity,
                head.SnapshotId, head.SampleTime);
        }
        var result = _rig.Calibrate(request);
        if (!result.Success) { CalibrationMessage = result.Error ?? "Saved calibration could not be restored."; return; }
        RememberBindings(request);
        for (int i = 0; i < 11; i++)
            if (_boundDevices[i] is null) { _sourceWeights[i] = 0.0f; _rig.SetSlotWeight((EHumanoidIKTarget)i, 0.0f); }
        HasCalibration = true;
        _calibratedMeasurement = saved.Measurement;
        _calibratedMeasurementMode = saved.Mode;
        CalibrationState = VrCalibrationState.Calibrated;
        CalibrationMessage = missing ? "Calibration restored. A bound tracker is missing; reconnect it or recalibrate." : "Calibration restored.";
        NotifyTrackingDiscontinuity();
    }
}
