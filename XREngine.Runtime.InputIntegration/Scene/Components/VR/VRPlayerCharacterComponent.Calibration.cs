using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Input;
using XREngine.Core;
using XREngine.Data.Components.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

public partial class VRPlayerCharacterComponent
{
    private EVrCalibrationState _calibrationState;
    public EVrCalibrationState CalibrationState { get => _calibrationState; private set => SetField(ref _calibrationState, value); }
    private string _calibrationMessage = "Set your height or arm span, then open calibration.";
    public string CalibrationMessage { get => _calibrationMessage; private set => SetField(ref _calibrationMessage, value); }
    public bool HasCommittedCalibration { get; private set; }
    private int _pendingCalibrationRequest;
    private bool _openPressed, _cancelPressed, _capturePressed;
    private float _leftCapture, _rightCapture;
    private EVrCalibrationState _previousCalibrationState;
    private EHumanoidPosePreviewMode _previousPreviewMode;
    private bool _previousSolverActive;
    private readonly List<(TransformBase Transform, Matrix4x4 Local)> _savedPose = [];
    private RuntimeVrTrackerPose[] _captureTrackers = new RuntimeVrTrackerPose[16];
    private RuntimeVrTrackingSnapshot _previousSnapshot;
    private readonly VrCalibrationStationaryWindow _stationaryWindow = new();
    private readonly VrCalibrationPose[] _captureSamples = new VrCalibrationPose[11];
    private int _pendingDiscontinuities;
    private RuntimeVrTrackingSnapshot _simulationSnapshot;
    private int _simulationTrackerCount;
    private bool _simulationSnapshotReady;
    private readonly List<VRDeviceTransformBase> _claimedSimulationSources = new(16);
    private XREngine.Components.Movement.IRuntimeCharacterMovementComponent? _discontinuityMovement;

    private void RegisterMovementDiscontinuities(bool unregister)
    {
        if (_discontinuityMovement is { } previous)
            previous.Teleported -= OnMovementTeleported;
        _discontinuityMovement = unregister ? null : GetCharacterMovement();
        if (_discontinuityMovement is { } current)
            current.Teleported += OnMovementTeleported;
    }
    private static void OnMovementTeleported() => RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.Teleport);

    /// <summary>Queues calibration for the simulation owner; input callbacks never mutate the rig.</summary>
    public bool BeginCalibration()
    {
        if (IsCalibrating)
            return false;
        Interlocked.Exchange(ref _pendingCalibrationRequest, 1);
        return true;
    }
    public void EndCalibration() => Interlocked.Exchange(ref _pendingCalibrationRequest, 2);
    public void CancelCalibration() => Interlocked.Exchange(ref _pendingCalibrationRequest, 3);

    private void ProcessCalibrationRequest()
    {
        ProcessCalibrationDiscontinuity();
        switch (Interlocked.Exchange(ref _pendingCalibrationRequest, 0))
        {
            case 1: BeginCalibrationImmediate(); break;
            case 2: CaptureCalibrationImmediate(); break;
            case 3: CancelCalibrationImmediate(); break;
        }
    }

    private void RegisterCalibrationInput(bool unregister)
    {
        // Direct runtime registration deliberately does not depend on desktop pawn possession.
        if (unregister)
            RuntimeVrDiscontinuityServices.Published -= OnCalibrationDiscontinuity;
        else
            RuntimeVrDiscontinuityServices.Published += OnCalibrationDiscontinuity;
        var input = RuntimeVrInputServices.Current;
        input.RegisterBoolAction("Global", "CalibrationOpen", OnCalibrationOpen, unregister);
        input.RegisterBoolAction("Global", "CalibrationCancel", OnCalibrationCancel, unregister);
        input.RegisterFloatAction("Global", "CalibrationCaptureLeft", OnCaptureLeft, unregister);
        input.RegisterFloatAction("Global", "CalibrationCaptureRight", OnCaptureRight, unregister);
    }
    private void OnCalibrationOpen(bool pressed)
    {
        if (pressed && !_openPressed)
            BeginCalibration();
        _openPressed = pressed;
    }
    private void OnCalibrationCancel(bool pressed)
    {
        if (pressed && !_cancelPressed)
            CancelCalibration();
        _cancelPressed = pressed;
    }
    private void OnCaptureLeft(float previous, float value) { _leftCapture = value; UpdateCaptureGesture(); }
    private void OnCaptureRight(float previous, float value) { _rightCapture = value; UpdateCaptureGesture(); }
    private void UpdateCaptureGesture()
    {
        bool pressed = _leftCapture >= 0.8f && _rightCapture >= 0.8f;
        if (pressed && !_capturePressed && IsCalibrating)
            EndCalibration();
        _capturePressed = pressed;
    }

    private void SaveCalibrationState(IHumanoidVrCalibrationRig humanoid, IVRIKSolverHandle solver)
    {
        LastHeadTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Head);
        LastHipsTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Hips);
        LastLeftHandTarget = humanoid.GetIKTarget(EHumanoidIKTarget.LeftHand);
        LastRightHandTarget = humanoid.GetIKTarget(EHumanoidIKTarget.RightHand);
        LastLeftFootTarget = humanoid.GetIKTarget(EHumanoidIKTarget.LeftFoot);
        LastRightFootTarget = humanoid.GetIKTarget(EHumanoidIKTarget.RightFoot);
        LastChestTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Chest);
        LastLeftElbowTarget = humanoid.GetIKTarget(EHumanoidIKTarget.LeftElbow);
        LastRightElbowTarget = humanoid.GetIKTarget(EHumanoidIKTarget.RightElbow);
        LastLeftKneeTarget = humanoid.GetIKTarget(EHumanoidIKTarget.LeftKnee);
        LastRightKneeTarget = humanoid.GetIKTarget(EHumanoidIKTarget.RightKnee);
        _previousCalibrationState = CalibrationState;
        _previousSolverActive = solver.IsActive;
        _previousPreviewMode = humanoid.PosePreviewMode;
        _savedPose.Clear();
        Save(humanoid.RootTransform);
        void Save(TransformBase transform)
        {
            _savedPose.Add((transform, transform.LocalMatrix));
            foreach (TransformBase child in transform.Children)
                Save(child);
        }
    }
    private void RestoreCalibrationState(IHumanoidVrCalibrationRig humanoid, IVRIKSolverHandle solver)
    {
        humanoid.PosePreviewMode = _previousPreviewMode;
        foreach (var saved in _savedPose)
            if (!saved.Transform.IsDestroyed)
            {
                saved.Transform.DeriveLocalMatrix(saved.Local);
                saved.Transform.RecalculateMatrices(true);
            }
        RestoreTargets(humanoid, LastHeadTarget, LastHipsTarget, LastLeftHandTarget, LastRightHandTarget,
            LastLeftFootTarget, LastRightFootTarget, LastChestTarget, LastLeftElbowTarget, LastRightElbowTarget, LastLeftKneeTarget, LastRightKneeTarget);
        solver.IsActive = _previousSolverActive;
        _savedPose.Clear();
        _stationaryWindow.Reset();
    }

    private bool CopyCaptureSnapshot(out RuntimeVrTrackingSnapshot snapshot, out int count)
    {
        snapshot = _simulationSnapshot;
        count = _simulationTrackerCount;
        return _simulationSnapshotReady;
    }

    private void PublishSimulationSnapshot()
    {
        ReleaseSimulationSnapshot();
        _simulationSnapshotReady = RuntimeVrStateServices.TryCopyTrackingSnapshot(_captureTrackers, out _simulationSnapshot, out _simulationTrackerCount);
        if (!_simulationSnapshotReady && _simulationTrackerCount > _captureTrackers.Length)
        {
            Array.Resize(ref _captureTrackers, _simulationTrackerCount);
            _simulationSnapshotReady = RuntimeVrStateServices.TryCopyTrackingSnapshot(_captureTrackers, out _simulationSnapshot, out _simulationTrackerCount);
        }
        if (!RuntimeVrStateServices.IsOpenXRActive)
            return;
        PublishSource(Headset, _simulationSnapshot.HeadPose, _simulationSnapshotReady && _simulationSnapshot.HeadValid);
        PublishSource(LeftController, _simulationSnapshot.LeftControllerPose, _simulationSnapshotReady && _simulationSnapshot.LeftControllerValid);
        PublishSource(RightController, _simulationSnapshot.RightControllerPose, _simulationSnapshotReady && _simulationSnapshot.RightControllerValid);
        if (GetTrackerCollection() is not { } collection)
            return;
        foreach (var pair in collection.Trackers.Values)
        {
            VRTrackerTransform tracker = pair.Item2;
            bool found = false;
            if (_simulationSnapshotReady)
                for (int i = 0; i < _simulationTrackerCount; i++)
                    if (string.Equals(tracker.TrackingIdentity, _captureTrackers[i].Info.PersistentPath, StringComparison.Ordinal))
                    {
                        PublishSource(tracker, _captureTrackers[i].LocalPose, _captureTrackers[i].Info.PoseCurrentlyUsable);
                        found = true;
                        break;
                    }
            if (!found)
                PublishSource(tracker, default, false);
        }
    }

    private void PublishSource(VRDeviceTransformBase? source, Matrix4x4 pose, bool valid)
    {
        if (source is null)
            return;
        if (source.PublishSimulationPose(this, _simulationSnapshot, pose, valid))
        {
            _claimedSimulationSources.Add(source);
            RefreshSourceHierarchy(source);
        }
        else
        {
            _simulationSnapshotReady = false;
            CalibrationMessage = "A tracking source belongs to another player rig. Check the rig's device references.";
            if (GetIKSolver() is { } solver)
                solver.IsActive = false;
        }
    }

    private static void RefreshSourceHierarchy(TransformBase source)
    {
        if (source.Parent is { } parent)
            RefreshSourceHierarchy(parent);
        source.RecalculateMatrices();
    }

    private void ReleaseSimulationSnapshot()
    {
        foreach (VRDeviceTransformBase source in _claimedSimulationSources)
            source.ReleaseSimulationPose(this);
        _claimedSimulationSources.Clear();
        _simulationSnapshotReady = false;
    }

    private void SampleCalibrationWindow()
    {
        if (!CopyCaptureSnapshot(out RuntimeVrTrackingSnapshot snapshot, out int count)
            || !TryCollectCapturePoses(snapshot, count, out int sampleCount))
        {
            _stationaryWindow.Reset();
            return;
        }
        _stationaryWindow.Observe(snapshot, _captureSamples.AsSpan(0, sampleCount));
        _previousSnapshot = snapshot;
    }

    private bool TryBuildCaptureSnapshot(IHumanoidVrCalibrationRig humanoid, out VrCalibrationPose[] poses)
    {
        poses = [];
        if (!CopyCaptureSnapshot(out RuntimeVrTrackingSnapshot snapshot, out int count)
            || !TryCollectCapturePoses(snapshot, count, out int sampleCount))
        {
            _stationaryWindow.Reset();
            CalibrationMessage = "The headset, both controllers, and selected trackers must be tracking. Try again when tracking returns.";
            return false;
        }
        // The exact publication used for capture ends the stationary window; do not read a second snapshot.
        _stationaryWindow.Observe(snapshot, _captureSamples.AsSpan(0, sampleCount));
        _previousSnapshot = snapshot;
        float tolerance = RuntimeVrStateServices.CalibrationHeadTiltToleranceDegrees;
        if (!VrCalibrationMath.IsHeadLevel(snapshot.HeadPose, tolerance))
        {
            CalibrationMessage = "Look straight ahead and keep your head level, then pull both triggers again.";
            return false;
        }
        if (!_stationaryWindow.IsStationary)
        {
            CalibrationMessage = "Hold still briefly, release both triggers, and try again.";
            return false;
        }
        poses = _captureSamples.AsSpan(0, sampleCount).ToArray();
        return true;
    }

    private bool TryCollectCapturePoses(in RuntimeVrTrackingSnapshot snapshot, int trackerCount, out int sampleCount)
    {
        sampleCount = 0;
        if (!snapshot.HeadValid || !snapshot.LeftControllerValid || !snapshot.RightControllerValid
            || Headset is null || LeftController is null || RightController is null || GetHumanoid() is not { } humanoid)
            return false;
        _captureSamples[sampleCount++] = CapturePose(Headset, snapshot.HeadPose);
        _captureSamples[sampleCount++] = CapturePose(LeftController, snapshot.LeftControllerPose);
        _captureSamples[sampleCount++] = CapturePose(RightController, snapshot.RightControllerPose);
        for (int slot = 0; slot < 11; slot++)
        {
            if (slot is 0 or 2 or 3 || humanoid.GetIKTarget((EHumanoidIKTarget)slot).tfm is not VRTrackerTransform tracker)
                continue;
            bool found = false;
            for (int i = 0; i < trackerCount; i++)
                if (_captureTrackers[i].Info.PoseCurrentlyUsable && string.Equals(_captureTrackers[i].Info.PersistentPath, tracker.TrackingIdentity, StringComparison.Ordinal))
                {
                    _captureSamples[sampleCount++] = CapturePose(tracker, _captureTrackers[i].LocalPose);
                    found = true;
                    break;
                }
            if (!found)
                return false;
        }
        return true;
    }

    private static VrCalibrationPose CapturePose(VRDeviceTransformBase source, Matrix4x4 localPose)
        => new(source, localPose * (source.LocalMatrixOffset ?? Matrix4x4.Identity) * source.ParentWorldMatrix);

    private void OnCalibrationDiscontinuity(VrPoseDiscontinuity discontinuity)
        => Interlocked.Or(ref _pendingDiscontinuities, 1 << (int)discontinuity.Kind);

    private void ProcessCalibrationDiscontinuity()
    {
        int pending = Interlocked.Exchange(ref _pendingDiscontinuities, 0);
        if (pending == 0)
            return;
        _stationaryWindow.Reset();
        // An ordinary movement cut must not overwrite a pending tracking-basis or avatar invalidation.
        const int invalidatingEvents = (1 << (int)EVrPoseDiscontinuity.Recenter)
            | (1 << (int)EVrPoseDiscontinuity.SessionGeneration)
            | (1 << (int)EVrPoseDiscontinuity.AvatarReplacement);
        if ((pending & invalidatingEvents) != 0)
        {
            if (IsCalibrating)
                CancelCalibrationImmediate();
            HasCommittedCalibration = false;
            CalibrationState = EVrCalibrationState.Uncalibrated;
            GetIKSolver()?.ClearTargets();
            CalibrationMessage = "Tracking space or avatar changed. Please recalibrate.";
        }
    }
}
