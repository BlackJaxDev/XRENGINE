using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Core;
using XREngine.Data.Components.Scene;
using XREngine.Input;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

public partial class VRPlayerCharacterComponent
{
    public string PlayerIdentity { get; set; } = "local-player-one";
    private string? _avatarIdentity;
    public string? AvatarIdentity
    {
        get => _avatarIdentity;
        set
        {
            if (SetField(ref _avatarIdentity, value) && HasCommittedCalibration)
                RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.AvatarReplacement);
        }
    }
    private void PrepareForAvatarReplacement()
    {
        if (IsCalibrating)
            CancelCalibrationImmediate();
        if (GetIKSolver() is { } previousSolver)
        {
            previousSolver.ClearTargets();
            previousSolver.IsActive = false;
        }
        HasCommittedCalibration = false;
        _attemptedSessionRestore = false;
        _missingStoredTrackers = 0;
        CalibrationState = EVrCalibrationState.Uncalibrated;
        RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.AvatarReplacement);
    }

    private bool _attemptedSessionRestore;
    private int _missingStoredTrackers;
    private int _restoreSourceSignature;

    private bool TryGetSessionScope(out VrCalibrationSessionScope scope, out VrBodyMeasurementKey measurement)
    {
        scope = default;
        if (!VrBodyMeasurementKey.TryFromSettings(RuntimeVrStateServices.PlayerSettings, out measurement)
            || string.IsNullOrWhiteSpace(AvatarIdentity)
            || !CopyCaptureSnapshot(out RuntimeVrTrackingSnapshot snapshot, out _))
            return false;
        scope = new(PlayerIdentity, AvatarIdentity, RuntimeVrInputServices.Current.ActiveRuntime,
            snapshot.SessionGeneration, snapshot.ReferenceSpaceVersion);
        return true;
    }

    private static string SourceIdentity(EHumanoidIKTarget slot, TransformBase source)
        => source is IVrTrackingPoseSource { TrackingIdentity: { Length: > 0 } identity } ? identity : slot switch
        {
            EHumanoidIKTarget.Head => "fixed-headset",
            EHumanoidIKTarget.LeftHand => "fixed-left-controller",
            EHumanoidIKTarget.RightHand => "fixed-right-controller",
            _ => string.Empty,
        };

    private void SaveCommittedSession()
    {
        if (!TryGetSessionScope(out var scope, out var measurement) || GetHumanoid() is not { } humanoid || GetIKSolver() is not { } solver)
            return;
        var slots = new List<VrStoredCalibrationSlot>(11);
        for (int i = 0; i < 11; i++)
        {
            EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
            if (slot is EHumanoidIKTarget.Head or EHumanoidIKTarget.LeftHand or EHumanoidIKTarget.RightHand)
                continue;
            if (humanoid.GetIKTarget(slot).tfm is { } source && solver.GetCalibratedTarget(slot) is { } target)
                slots.Add(new(slot, SourceIdentity(slot, source), target.LocalMatrix));
        }
        _missingStoredTrackers = 0;
        if (!VrCalibrationSessionStore.Shared.TrySave(scope, measurement, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(slots), out string notice))
            CalibrationMessage = notice;
    }

    private void EnsureInitialTrackingRig()
    {
        if (IsCalibrating || GetIKSolver() is not { } solver || GetHumanoid() is not { } humanoid
            || Headset is null || !Headset.PoseCurrentlyUsable)
            return;
        bool needsFixedTargets = solver.GetCalibratedTarget(EHumanoidIKTarget.Head) is null
            || LeftController?.PoseCurrentlyUsable == true && solver.GetCalibratedTarget(EHumanoidIKTarget.LeftHand) is null
            || RightController?.PoseCurrentlyUsable == true && solver.GetCalibratedTarget(EHumanoidIKTarget.RightHand) is null;
        if (!HasCommittedCalibration && needsFixedTargets)
        {
            Vector3 eyeOffset = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
            humanoid.SetIKTarget(EHumanoidIKTarget.Head, Headset, GetFixedEyeToHeadOffset(humanoid, eyeOffset));
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, LeftController, LeftControllerOffset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightHand, RightController, RightControllerOffset);
            VrCalibrationResult initial = RuntimeVRIKCalibrator.Calibrate(solver, RuntimeVrStateServices.CalibrationSettings,
                Headset, null, LeftController?.PoseCurrentlyUsable == true ? LeftController : null,
                RightController?.PoseCurrentlyUsable == true ? RightController : null);
            if (!initial.Success)
                return;
            solver.IsActive = true;
        }
        // Fixed controller targets must be established before body-session restoration, so a controller
        // appearing after the headset never gets stranded behind the committed-body guard.
        if (LeftController?.PoseCurrentlyUsable != true || RightController?.PoseCurrentlyUsable != true)
            return;
        if ((_missingStoredTrackers == 0 && (_attemptedSessionRestore || HasCommittedCalibration))
            || !TryGetSessionScope(out var scope, out var measurement))
            return;
        int signature = 17;
        if (GetTrackerCollection() is { } currentTrackers)
            foreach (var pair in currentTrackers.Trackers.Values)
                signature = HashCode.Combine(signature, pair.Item2.TrackingIdentity, pair.Item2.PoseCurrentlyUsable);
        if (_attemptedSessionRestore && signature == _restoreSourceSignature)
            return;
        _restoreSourceSignature = signature;
        var sources = new List<VrCalibrationSessionSource>(11);
        if (Headset is { } head)
            sources.Add(new(SourceIdentity(EHumanoidIKTarget.Head, head), head, head.PoseCurrentlyUsable));
        if (LeftController is { } left)
            sources.Add(new(SourceIdentity(EHumanoidIKTarget.LeftHand, left), left, left.PoseCurrentlyUsable));
        if (RightController is { } right)
            sources.Add(new(SourceIdentity(EHumanoidIKTarget.RightHand, right), right, right.PoseCurrentlyUsable));
        if (GetTrackerCollection() is { } collection)
            foreach (var pair in collection.Trackers.Values)
                sources.Add(new(pair.Item2.TrackingIdentity ?? string.Empty, pair.Item2, pair.Item2.PoseCurrentlyUsable));
        var restored = new VrCalibrationTarget[11];
        int fixedCount = 0;
        AddFixed(EHumanoidIKTarget.Head);
        AddFixed(EHumanoidIKTarget.LeftHand);
        AddFixed(EHumanoidIKTarget.RightHand);
        void AddFixed(EHumanoidIKTarget slot)
        {
            if (humanoid.GetIKTarget(slot).tfm is { } source && solver.GetCalibratedTarget(slot) is { } target)
                restored[fixedCount++] = new(slot, source, target.LocalMatrix);
        }
        if (!VrCalibrationSessionStore.Shared.TryRestore(scope, measurement,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sources), restored.AsSpan(fixedCount), out int written, out int missing, out string notice))
        {
            _attemptedSessionRestore = true;
            if (!string.IsNullOrWhiteSpace(notice))
                CalibrationMessage = notice;
            return;
        }
        VrCalibrationResult result = solver.RestoreCalibration(restored.AsSpan(0, fixedCount + written), RuntimeVrStateServices.CalibrationSettings);
        if (!result.Success)
            return;
        _attemptedSessionRestore = true;
        _missingStoredTrackers = missing;
        HasCommittedCalibration = true;
        CalibrationState = EVrCalibrationState.Calibrated;
        CalibrationMessage = missing > 0 ? notice : "Calibration restored for the same avatar and trackers.";
    }
}
