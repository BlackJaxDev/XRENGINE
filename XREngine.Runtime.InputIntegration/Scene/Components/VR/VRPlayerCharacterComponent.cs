using OpenVR.NET.Devices;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Core;
using XREngine.Core.Attributes;
using XREngine.Data.Components.Scene;
using XREngine.Extensions;
using XREngine.Input;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR
{
    public partial class VRPlayerCharacterComponent : XRComponent
    {
        private bool _isCalibrating = false;
        public bool IsCalibrating
        {
            get => _isCalibrating;
            private set => SetField(ref _isCalibrating, value);
        }

        private float _calibrationRadius = 0.25f;
        public float CalibrationRadius
        {
            get => _calibrationRadius;
            set => SetField(ref _calibrationRadius, value);
        }

        private Matrix4x4 _leftControllerOffset = Matrix4x4.Identity;
        public Matrix4x4 LeftControllerOffset
        {
            get => _leftControllerOffset;
            set => SetField(ref _leftControllerOffset, value);
        }

        private Matrix4x4 _rightControllerOffset = Matrix4x4.Identity;
        public Matrix4x4 RightControllerOffset
        {
            get => _rightControllerOffset;
            set => SetField(ref _rightControllerOffset, value);
        }

        private XRComponent? _humanoidComponent;
        public XRComponent? HumanoidComponent
        {
            get => _humanoidComponent;
            set
            {
                if (SetField(ref _humanoidComponent, value))
                    Interlocked.Or(ref _pendingActions, 256);
            }
        }

        private XRComponent? _heightScaleComponent;
        public XRComponent? HeightScaleComponent
        {
            get => _heightScaleComponent;
            set => SetField(ref _heightScaleComponent, value);
        }

        private XRComponent? _characterMovementComponent;
        public XRComponent? CharacterMovementComponent
        {
            get => _characterMovementComponent;
            set => SetField(ref _characterMovementComponent, value);
        }

        private VRTrackerCollectionComponent? _trackerCollection;
        public VRTrackerCollectionComponent? TrackerCollection
        {
            get => _trackerCollection;
            set => SetField(ref _trackerCollection, value);
        }

        private VRHeadsetTransform? _headset;
        public VRHeadsetTransform? Headset
        {
            get => _headset;
            set => SetField(ref _headset, value);
        }

        private VRControllerTransform? _leftController;
        public VRControllerTransform? LeftController
        {
            get => _leftController;
            set => SetField(ref _leftController, value);
        }

        private VRControllerTransform? _rightController;
        public VRControllerTransform? RightController
        {
            get => _rightController;
            set => SetField(ref _rightController, value);
        }

        private XRComponent? _eyesModel;
        public XRComponent? EyesModel
        {
            get => _eyesModel;
            set => SetField(ref _eyesModel, value);
        }

        private string? _eyeLBoneName;
        public string? EyeLBoneName
        {
            get => _eyeLBoneName;
            set => SetField(ref _eyeLBoneName, value);
        }

        private string? _eyeRBoneName;
        public string? EyeRBoneName
        {
            get => _eyeRBoneName;
            set => SetField(ref _eyeRBoneName, value);
        }

        public IHumanoidVrCalibrationRig? GetHumanoid()
            => HumanoidComponent as IHumanoidVrCalibrationRig
            ?? SceneNode.GetComponents<XRComponent>().Where(component => component != this).OfType<IHumanoidVrCalibrationRig>().FirstOrDefault();

        public IVRIKSolverHandle? GetIKSolver()
            => IKSolver as IVRIKSolverHandle
            ?? SceneNode.GetComponents<XRComponent>().Where(component => component != this).OfType<IVRIKSolverHandle>().FirstOrDefault();

        public VRTrackerCollectionComponent? GetTrackerCollection()
            => TrackerCollection ?? GetSiblingComponent<VRTrackerCollectionComponent>();

        public IRuntimeCharacterMovementComponent? GetCharacterMovement()
            => CharacterMovementComponent as IRuntimeCharacterMovementComponent
            ?? SceneNode.GetComponents<XRComponent>().Where(component => component != this).OfType<IRuntimeCharacterMovementComponent>().FirstOrDefault();

        public IRuntimeVrHeightScaleComponent? GetHeightScaleComponent()
            => HeightScaleComponent as IRuntimeVrHeightScaleComponent
            ?? SceneNode.GetComponents<XRComponent>().Where(component => component != this).OfType<IRuntimeVrHeightScaleComponent>().FirstOrDefault();

        private string? _eyesModelResolveName = "face";
        public string? EyesModelResolveName
        {
            get => _eyesModelResolveName;
            set => SetField(ref _eyesModelResolveName, value);
        }

        private string? _headsetResolveName = "VRHeadsetNode";
        public string? HeadsetResolveName
        {
            get => _headsetResolveName;
            set => SetField(ref _headsetResolveName, value);
        }

        private string? _leftControllerResolveName = "VRLeftControllerNode";
        public string? LeftControllerResolveName
        {
            get => _leftControllerResolveName;
            set => SetField(ref _leftControllerResolveName, value);
        }

        private string? _rightControllerResolveName = "VRRightControllerNode";
        public string? RightControllerResolveName
        {
            get => _rightControllerResolveName;
            set => SetField(ref _rightControllerResolveName, value);
        }

        private string? _trackerCollectionResolveName = "VRTrackerCollectionNode";
        public string? TrackerCollectionResolveName
        {
            get => _trackerCollectionResolveName;
            set => SetField(ref _trackerCollectionResolveName, value);
        }

        private void ResolveDependencies()
        {
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            if (humanoid is null)
                return;

            if (EyesModel is null && EyesModelResolveName is not null)
            {
                SceneNode? modelNode = humanoid.SceneNode.FindDescendant(x => x.Name?.Contains(EyesModelResolveName, StringComparison.InvariantCultureIgnoreCase) ?? false);
                EyesModel = modelNode?.GetComponent("ModelComponent");
            }

            if (Headset is null && HeadsetResolveName is not null)
                Headset = SceneNode.FindDescendantByName(HeadsetResolveName)?.Transform as VRHeadsetTransform;

            if (LeftController is null && LeftControllerResolveName is not null)
                LeftController = SceneNode.FindDescendantByName(LeftControllerResolveName)?.Transform as VRControllerTransform;

            if (RightController is null && RightControllerResolveName is not null)
                RightController = SceneNode.FindDescendantByName(RightControllerResolveName)?.Transform as VRControllerTransform;

            if (TrackerCollection is null && TrackerCollectionResolveName is not null)
                TrackerCollection = SceneNode.FindDescendantByName(TrackerCollectionResolveName)?.GetComponent<VRTrackerCollectionComponent>();
        }

        private XRComponent? _ikSolver;
        public XRComponent? IKSolver { get => _ikSolver; set => SetField(ref _ikSolver, value); }
        private Transform? _playspaceRoot;
        public Transform? PlayspaceRoot { get => _playspaceRoot; set => SetField(ref _playspaceRoot, value); }
        private Transform? _avatarRoot;
        public Transform? AvatarRoot { get => _avatarRoot; set => SetField(ref _avatarRoot, value); }
        private TransformBase? _playerRoot;
        public TransformBase? PlayerRoot { get => _playerRoot; set => SetField(ref _playerRoot, value); }
        private UserSettings? _playerSettings;
        public UserSettings? PlayerSettings { get => _playerSettings; set => SetField(ref _playerSettings, value); }
        private VrCalibrationState _calibrationState;
        public VrCalibrationState CalibrationState { get => _calibrationState; private set => SetField(ref _calibrationState, value); }
        private string _calibrationMessage = "Open calibration to bind body trackers.";
        public string CalibrationMessage { get => _calibrationMessage; private set => SetField(ref _calibrationMessage, value); }
        public bool HasCalibration { get; private set; }
        public long DiscontinuityGeneration { get; private set; }
        public event Action? TrackingDiscontinuity;
        public TransformBase? SpectatorHipsTransform => _rig is not null && _rig.TryGetSlot(EHumanoidIKTarget.Hips, out var slot) && slot.Weight > 0.01f ? slot.Target : null;
        private IHumanoidVrCalibrationRig? _humanoid;
        private IVRIKCalibrationHandle? _rig;
        private int _pendingActions;
        private bool _previousSolverActive;
        private Matrix4x4 _previousAvatarLocal;
        private readonly VRDeviceTransformBase?[] _boundDevices = new VRDeviceTransformBase?[11];
        private readonly string?[] _boundIdentities = new string?[11];
        private readonly float[] _sourceWeights = new float[11];
        private readonly double[] _lastUsable = new double[11];
        private readonly Matrix4x4[] _previousSamples = new Matrix4x4[11];
        private readonly Matrix4x4[] _currentSamples = new Matrix4x4[11];
        private readonly VRDeviceTransformBase?[] _stationaryDevices = new VRDeviceTransformBase?[11];
        private readonly Matrix4x4[] _sourceSamples = new Matrix4x4[11];
        private readonly bool[] _sourceSampleValid = new bool[11];
        private readonly bool[] _estimateSampleValid = new bool[11];
        private readonly float[] _estimatorWeights = new float[11];
        private readonly Matrix4x4[] _trackedGoals = new Matrix4x4[11];
        private readonly Matrix4x4[] _estimatedGoals = new Matrix4x4[11];
        private IVrBodyPoseSource? _bodyPoseSource;
        public IVrBodyPoseSource? BodyPoseSource { get => _bodyPoseSource; set => SetField(ref _bodyPoseSource, value); }
        private double _stationarySince;
        private bool _hasPreviousSample;
        private bool _headHandsInitialized;
        private double _nextHeadHandsAttempt;
        private double _lastTickTime;
        private Vector3 _lastPlayerPosition;
        private Quaternion _lastPlayerRotation = Quaternion.Identity;
        private IRuntimeCharacterMovementComponent? _movement;
        private float _calibratedMeasurement;
        private EBodyMeasurementMode _calibratedMeasurementMode;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            InitializeRig();
            RuntimeVrInputServices.CalibrationActionPressed += OnCalibrationAction;
            RuntimeVrStateServices.TrackingBasisChanged += OnTrackingBasisChanged;
            RuntimeVrStateServices.SessionGenerationChanged += OnSessionGenerationChanged;
            _lastTickTime = Now;
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateTick);
        }

        /// <summary>Resolves configured rig references after pawn construction without starting calibration.</summary>
        public void InitializeRig()
        {
            ResolveDependencies();
            _humanoid = GetHumanoid();
            _rig = GetIKSolver() as IVRIKCalibrationHandle;
            _movement = GetCharacterMovement();
            AvatarRoot ??= _humanoid?.RootTransform as Transform;
            PlayspaceRoot ??= Headset?.Parent as Transform;
            PlayerRoot ??= PlayspaceRoot?.Parent;
            GetHeightScaleComponent()?.CalculateEyeOffsetFromHead(EyesModel, EyeLBoneName, EyeRBoneName);
            GetHeightScaleComponent()?.MeasureAvatarHeight();
        }

        protected override void OnComponentDeactivated()
        {
            ClearMeasurementEntry();
            RuntimeVrInputServices.CalibrationActionPressed -= OnCalibrationAction;
            RuntimeVrStateServices.TrackingBasisChanged -= OnTrackingBasisChanged;
            RuntimeVrStateServices.SessionGenerationChanged -= OnSessionGenerationChanged;
            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateTick);
            if (IsCalibrating) CancelOnSceneThread();
            SaveSessionCalibration();
            base.OnComponentDeactivated();
        }

        private static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        private void OnTrackingBasisChanged() => Interlocked.Or(ref _pendingActions, 8);
        private void OnSessionGenerationChanged() => Interlocked.Or(ref _pendingActions, 16);
        private void OnCalibrationAction(RuntimeVrCalibrationAction action)
            => Interlocked.Or(ref _pendingActions, action switch
            {
                RuntimeVrCalibrationAction.Open => 1,
                RuntimeVrCalibrationAction.Cancel => 2,
                RuntimeVrCalibrationAction.Capture => 4,
                RuntimeVrCalibrationAction.MeasurementLeftSelect => 32,
                RuntimeVrCalibrationAction.MeasurementRightSelect => 64,
                RuntimeVrCalibrationAction.MeasurementBothSelect => 128,
                _ => 0,
            });

        public bool BeginCalibration()
        {
            Interlocked.Or(ref _pendingActions, 1);
            return Headset is not null && GetIKSolver() is IVRIKCalibrationHandle;
        }
        public void CancelCalibration() => Interlocked.Or(ref _pendingActions, 2);
        public void EndCalibration() => Interlocked.Or(ref _pendingActions, 4);

        private void OpenOnSceneThread()
        {
            if (IsCalibrating || _rig is null || _humanoid is null || AvatarRoot is null)
                return;
            _previousSolverActive = _rig.IsActive;
            _previousAvatarLocal = AvatarRoot.LocalMatrix;
            _rig.IsActive = false;
            IsCalibrating = true;
            CalibrationState = VrCalibrationState.Calibrating;
            _hasPreviousSample = false;
            _stationarySince = Now;
            RuntimeVrStateServices.RequestTrackerRefreshForCalibration();
            VRHeightScaleComponent? scale = HeightScaleComponent as VRHeightScaleComponent;
            CalibrationMessage = scale?.HasPlayerMeasurement == true
                ? "Stand straight in the footprints, look ahead, then pull both triggers."
                : scale?.MeasurementNotice ?? "Hold the calibration menu button to enter height or arm span, then stand in the footprints.";
        }

        private void CancelOnSceneThread()
        {
            if (!IsCalibrating) return;
            IsCalibrating = false;
            _rig?.EndCalibrationPose();
            if (AvatarRoot is { } root)
            {
                Vector3 currentScale = root.Scale;
                root.DeriveLocalMatrix(_previousAvatarLocal);
                root.Scale = currentScale;
            }
            if (_rig is not null) _rig.IsActive = _previousSolverActive;
            CalibrationState = HasCalibration ? VrCalibrationState.Calibrated : VrCalibrationState.Uncalibrated;
            CalibrationMessage = "Calibration cancelled; previous tracking restored.";
        }

        private void FailCapture(string message)
        {
            CancelOnSceneThread();
            CalibrationState = VrCalibrationState.Failed;
            CalibrationMessage = message;
        }

        private void UpdateTick()
        {
            if ((Volatile.Read(ref _pendingActions) & 256) != 0)
            {
                Interlocked.And(ref _pendingActions, ~256);
                if (_humanoid is not null && !ReferenceEquals(_humanoid, HumanoidComponent))
                {
                    InvalidateTrackingBasis();
                    CalibrationMessage = "Avatar changed. Reopen calibration.";
                }
                InitializeRig();
            }
            if (_humanoid is null || _rig is null) return;
            (HeightScaleComponent as VRHeightScaleComponent)?.RefreshMeasurement();
            if (HasCalibration && !IsCalibrating && (_calibratedMeasurement != MeasurementInUse() ||
                _calibratedMeasurementMode != PlayerSettings?.BodyMeasurementMode))
            {
                InvalidateTrackingBasis();
                CalibrationMessage = "Body measurements changed. Reopen calibration.";
            }
            double now = Now;
            float dt = (float)Math.Clamp(now - _lastTickTime, 0.0, 0.1);
            _lastTickTime = now;
            int actions = Interlocked.Exchange(ref _pendingActions, 0);
            UpdateMeasurementInput(actions, now);
            if ((actions & 8) != 0) InvalidateTrackingBasis();
            if ((actions & 16) != 0)
            {
                if (IsCalibrating) CancelOnSceneThread();
                Array.Clear(_sourceWeights);
                for (int i = 0; i < 11; i++) _rig.SetSlotWeight((EHumanoidIKTarget)i, 0.0f);
                NotifyTrackingDiscontinuity();
            }
            if ((actions & 2) != 0) CancelOnSceneThread();
            else if ((actions & 1) != 0) OpenOnSceneThread();
            if (IsCalibrating)
            {
                if (Headset?.TryGetCurrentWorldPose(out Matrix4x4 headWorld, out _, out _) == true)
                {
                    VrCalibrationResult poseResult = _rig.ApplyCanonicalCalibrationPose(headWorld,
                        GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero);
                    if (!poseResult.Success) { FailCapture(poseResult.Error ?? "Avatar pose is unavailable."); return; }
                }
                UpdateBindingPreview();
                if (RuntimeVrStateServices.IsTrackerRefreshPending)
                    CalibrationMessage = "New trackers are being acquired. Wait for tracking to resume, then reopen calibration.";
                UpdateStationaryWindow(now);
                if ((actions & 4) != 0 && !IsEditingMeasurement) CaptureOnSceneThread();
                return;
            }
            if (!_headHandsInitialized) InitializeHeadAndHands();
            UpdateSources(now, dt);
            DetectDiscontinuity();
        }

        private void InitializeHeadAndHands()
        {
            double now = Now;
            if (now < _nextHeadHandsAttempt) return;
            if (Headset?.TryGetCurrentWorldPose(out _, out _, out _) != true ||
                LeftController?.TryGetCurrentWorldPose(out _, out _, out _) != true ||
                RightController?.TryGetCurrentWorldPose(out _, out _, out _) != true)
                return;
            _nextHeadHandsAttempt = now + 0.5;
            if (_rig is null || !TryBuildRequest(false, out VrCalibrationRequest request, out _)) return;
            VrCalibrationResult result = _rig.Calibrate(request);
            if (!result.Success) { CalibrationMessage = result.Error ?? "Avatar rig is unavailable."; return; }
            RememberBindings(request);
            _headHandsInitialized = true;
            _rig.IsActive = true;
            RestoreSessionCalibration();
        }

        private void CaptureOnSceneThread()
        {
            if (_rig is null || !IsCalibrating) return;
            if (RuntimeVrStateServices.IsTrackerRefreshPending)
            {
                CalibrationMessage = "Tracker discovery is still pending. Wait for tracking to resume before capturing.";
                return;
            }
            if (HeightScaleComponent is not VRHeightScaleComponent scale || !scale.HasPlayerMeasurement)
            {
                FailCapture((HeightScaleComponent as VRHeightScaleComponent)?.MeasurementNotice
                    ?? "Enter your standing height or arm span in VR Body settings, then reopen calibration.");
                return;
            }
            if (!TryBuildRequest(true, out VrCalibrationRequest request, out string error))
            {
                FailCapture(error);
                return;
            }
            Matrix4x4 head = request.Slots[(int)EHumanoidIKTarget.Head]!.Value.DeviceWorld;
            Vector3 up = Vector3.Normalize(new Vector3(head.M21, head.M22, head.M23));
            float tilt = MathF.Acos(Math.Clamp(Vector3.Dot(up, Vector3.UnitY), -1.0f, 1.0f)) * 180.0f / MathF.PI;
            if (tilt > (PlayerSettings?.CalibrationHeadTiltTolerance ?? 10.0f))
            {
                FailCapture("Look straight ahead with your head level, then reopen calibration.");
                return;
            }
            if (!_hasPreviousSample || Now - _stationarySince < 0.15)
            {
                FailCapture("Hold your headset, controllers and trackers still briefly, then retry calibration.");
                return;
            }
            VrCalibrationResult result;
            try { result = _rig.Calibrate(request); }
            catch (Exception ex) { FailCapture("Calibration failed: " + ex.Message); return; }
            if (!result.Success) { FailCapture(result.Error ?? "Calibration failed."); return; }
            RememberBindings(request);
            IsCalibrating = false;
            _rig.EndCalibrationPose();
            _rig.IsActive = true;
            HasCalibration = true;
            _calibratedMeasurement = MeasurementInUse();
            _calibratedMeasurementMode = PlayerSettings?.BodyMeasurementMode ?? EBodyMeasurementMode.Height;
            _headHandsInitialized = true;
            CalibrationState = VrCalibrationState.Calibrated;
            CalibrationMessage = "Calibration complete.";
            if (PlayerSettings?.BodyMeasurementMode == EBodyMeasurementMode.Height && PlayspaceRoot is not null)
            {
                float eyeHeight = Vector3.Transform(head.Translation, PlayspaceRoot.InverseWorldMatrix).Y;
                if (MathF.Abs(eyeHeight - PlayerSettings.PlayerHeight * VRHeightScaleComponent.StandingHeightToEyeHeight) > 0.15f)
                    CalibrationMessage = "Calibration complete. Measured eye height differs from your standing-height setting; check the setting and floor origin.";
            }
            SaveSessionCalibration();
            NotifyTrackingDiscontinuity();
        }

        private bool TryBuildRequest(bool includeTrackers, out VrCalibrationRequest request, out string error)
        {
            request = new VrCalibrationRequest
            {
                Settings = RuntimeVrStateServices.CalibrationSettings,
                RequireLevelHead = includeTrackers,
                HeadTiltTolerance = PlayerSettings?.CalibrationHeadTiltTolerance,
                HeadsetToEyes = Matrix4x4.CreateTranslation(-(GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero)),
                LeftGripToWrist = LeftControllerOffset == Matrix4x4.Identity ? VrControllerWristOffsets.ForHand(RuntimeVrStateServices.GetCurrentInteractionProfile(true), true) : LeftControllerOffset,
                RightGripToWrist = RightControllerOffset == Matrix4x4.Identity ? VrControllerWristOffsets.ForHand(RuntimeVrStateServices.GetCurrentInteractionProfile(false), false) : RightControllerOffset,
            };
            error = "";
            long snapshot = -1;
            long sampleTime = 0;
            if (includeTrackers && !_bindingSamplesCoherent)
            {
                error = "Tracking samples changed during assignment. Hold still and retry.";
                return false;
            }
            for (int i = 0; i < 11; i++)
            {
                VRDeviceTransformBase? device = i switch
                {
                    (int)EHumanoidIKTarget.Head => Headset,
                    (int)EHumanoidIKTarget.LeftHand => LeftController,
                    (int)EHumanoidIKTarget.RightHand => RightController,
                    _ => includeTrackers ? _proposedDevices[i] : null,
                };
                bool required = i == 0 || i == 2 || i == 3;
                if (device is null)
                {
                    if (required) { error = "Headset and both controllers must be connected."; return false; }
                    continue;
                }
                Matrix4x4 world = default;
                long currentSnapshot = _bindingSnapshot;
                long currentSampleTime = _bindingSampleTime;
                bool usable = false;
                if (includeTrackers && !required)
                {
                    foreach (var sample in _bindingSamples)
                        if (ReferenceEquals(sample.Device, device)) { world = sample.World; usable = true; break; }
                }
                else usable = device.TryGetCurrentWorldPose(out world, out currentSnapshot, out currentSampleTime);
                if (!usable)
                {
                    error = required ? "Headset and both controllers must be tracking." : "A tracker lost tracking during capture. Reopen calibration and retry.";
                    return false;
                }
                if (snapshot >= 0 && (snapshot != currentSnapshot || sampleTime != currentSampleTime))
                {
                    error = "Tracking samples changed during capture. Hold still and retry.";
                    return false;
                }
                snapshot = currentSnapshot;
                sampleTime = currentSampleTime;
                request.Slots[i] = new VrCalibrationCapture(device, world, (device as VRTrackerTransform)?.SessionIdentity,
                    currentSnapshot, currentSampleTime);
            }
            return true;
        }

        private void RememberBindings(VrCalibrationRequest request)
        {
            for (int i = 0; i < 11; i++)
            {
                _boundDevices[i] = request.Slots[i]?.Device as VRDeviceTransformBase;
                _boundIdentities[i] = request.Slots[i]?.Identity;
                _sourceWeights[i] = request.Slots[i]?.Device is not null ? 1.0f : 0.0f;
                _lastUsable[i] = Now;
                _estimatorWeights[i] = 0.0f;
                if (_rig?.TryGetSlot((EHumanoidIKTarget)i, out var slot) == true)
                    _trackedGoals[i] = slot.Target.WorldMatrix;
            }
        }

        private void UpdateSources(double now, float dt)
        {
            if (_rig is null) return;
            long snapshot = -1;
            long sampleTime = 0;
            bool coherent = true;
            // Read all sources before publishing any target, so consumers never see a mixed simulation frame.
            bool missingBoundTracker = false;
            for (int i = 0; i < 11; i++)
            {
                VRDeviceTransformBase? device = _boundIdentities[i] is { } identity ? FindTracker(identity) : _boundDevices[i];
                long currentSnapshot = 0;
                long currentSampleTime = 0;
                _sourceSampleValid[i] = device is not null && device.TryGetCurrentWorldPose(out _sourceSamples[i], out currentSnapshot, out currentSampleTime);
                if (!_sourceSampleValid[i]) continue;
                if (snapshot >= 0 && (snapshot != currentSnapshot || sampleTime != currentSampleTime)) { coherent = false; break; }
                snapshot = currentSnapshot;
                sampleTime = currentSampleTime;
                _boundDevices[i] = device;
            }
            if (!coherent)
            {
                // Repeatedly crossing publications must degrade tracking instead of holding full weight forever.
                Array.Clear(_sourceSampleValid);
                snapshot = -1;
            }
            // Estimates share the pre-locomotion world basis of the sampled tracking publication.
            for (int i = 0; i < 11; i++)
            {
                _estimateSampleValid[i] = false;
                if (snapshot >= 0 && i is not (0 or 2 or 3) &&
                    BodyPoseSource?.TryGetTarget((EHumanoidIKTarget)i, snapshot, out Matrix4x4 estimate) == true)
                {
                    _estimateSampleValid[i] = true;
                    _estimatedGoals[i] = estimate;
                }
            }
            MovePlayer();
            float blend = dt / (PlayerSettings?.TrackingSourceBlendSeconds ?? 0.25f);
            for (int i = 0; i < 11; i++)
            {
                EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
                if (_sourceSampleValid[i])
                {
                    if (_boundIdentities[i] is { } boundIdentity)
                        _rig.RebindSlotDevice(slot, _boundDevices[i]!, boundIdentity);
                    _lastUsable[i] = now;
                    _sourceWeights[i] = MathF.Min(1.0f, _sourceWeights[i] + blend);
                    _rig.UpdateSlot(slot, _sourceSamples[i], _sourceWeights[i]);
                    if (_rig.TryGetSlot(slot, out var tracked)) _trackedGoals[i] = tracked.Target.WorldMatrix;
                }
                else if (now - _lastUsable[i] > (PlayerSettings?.TrackingLossHoldSeconds ?? 0.15f))
                {
                    missingBoundTracker |= _boundIdentities[i] is not null;
                    _sourceWeights[i] = MathF.Max(0.0f, _sourceWeights[i] - blend);
                    _rig.SetSlotWeight(slot, _sourceWeights[i]);
                }
                if (_estimateSampleValid[i])
                {
                    _estimatorWeights[i] = MathF.Min(1.0f, _estimatorWeights[i] + blend);
                }
                else _estimatorWeights[i] = MathF.Max(0.0f, _estimatorWeights[i] - blend);
                float trackerWeight = _sourceWeights[i];
                float estimateWeight = (1.0f - trackerWeight) * _estimatorWeights[i];
                float totalWeight = trackerWeight + estimateWeight;
                if (estimateWeight > 0.0f)
                {
                    Matrix4x4 blended = BlendPose(_estimatedGoals[i], _trackedGoals[i], trackerWeight / totalWeight);
                    _rig.UpdateEstimatedSlot(slot, blended, totalWeight);
                }
            }
            const string missingMessage = "A bound tracker is unavailable. Reconnect it or recalibrate after swapping trackers.";
            if (HasCalibration && CalibrationState == VrCalibrationState.Calibrated)
            {
                if (missingBoundTracker) CalibrationMessage = missingMessage;
                else if (CalibrationMessage == missingMessage) CalibrationMessage = "Tracking restored.";
            }
        }

        private static Matrix4x4 BlendPose(Matrix4x4 a, Matrix4x4 b, float weight)
        {
            if (!Matrix4x4.Decompose(a, out _, out Quaternion ar, out Vector3 ap) ||
                !Matrix4x4.Decompose(b, out _, out Quaternion br, out Vector3 bp)) return b;
            return Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ar, br, weight)) *
                Matrix4x4.CreateTranslation(Vector3.Lerp(ap, bp, weight));
        }

        private void UpdateStationaryWindow(double now)
        {
            bool allValid = true;
            bool moved = !_hasPreviousSample;
            long snapshot = -1;
            long sampleTime = 0;
            for (int i = 0; i < 11; i++)
            {
                VRDeviceTransformBase? device = i switch { 0 => Headset, 2 => LeftController, 3 => RightController, _ => _proposedDevices[i] };
                if (!ReferenceEquals(device, _stationaryDevices[i])) moved = true;
                _stationaryDevices[i] = device;
                if (device is null)
                {
                    if (i is 0 or 2 or 3) allValid = false;
                    continue;
                }
                if (!device.TryGetCurrentWorldPose(out Matrix4x4 pose, out long currentSnapshot, out long currentSampleTime)) { allValid = false; continue; }
                if (snapshot >= 0 && (snapshot != currentSnapshot || sampleTime != currentSampleTime)) allValid = false;
                snapshot = currentSnapshot;
                sampleTime = currentSampleTime;
                if (_hasPreviousSample && (Vector3.DistanceSquared(_previousSamples[i].Translation, pose.Translation) > 0.0001f ||
                    RotationDistance(_previousSamples[i], pose) > 0.035f)) moved = true;
                _currentSamples[i] = pose;
            }
            if (!allValid || moved)
            {
                _stationarySince = now;
                Array.Copy(_currentSamples, _previousSamples, 11);
            }
            _hasPreviousSample = allValid;
        }

        private static float RotationDistance(Matrix4x4 a, Matrix4x4 b)
        {
            if (!Matrix4x4.Decompose(a, out _, out Quaternion ar, out _) || !Matrix4x4.Decompose(b, out _, out Quaternion br, out _)) return float.PositiveInfinity;
            return 2.0f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(ar, br)), 0.0f, 1.0f));
        }

        private void MovePlayer()
        {
            if (_humanoid is null || PlayspaceRoot is null || AvatarRoot is null || _rig is null) return;
            // Losing a bound hips source must not turn head leaning into body locomotion.
            int index = _boundIdentities[(int)EHumanoidIKTarget.Hips] is not null
                ? (int)EHumanoidIKTarget.Hips : (int)EHumanoidIKTarget.Head;
            if (!_sourceSampleValid[index] || !_rig.TryGetSlot((EHumanoidIKTarget)index, out var slot)) return;
            // Consume this simulation sample before publishing targets. The captured offset is applied once.
            Matrix4x4 relative = slot.DeviceToTargetOffset * _sourceSamples[index] * PlayspaceRoot.InverseWorldMatrix;
            Vector3 offset = relative.Translation;
            offset.Y = 0.0f;
            if (offset.LengthSquared() < 0.000001f) return;
            IRuntimeCharacterMovementComponent? movement = _movement;
            if (movement is null) return;
            Vector3 oldPosition = PlayspaceRoot.WorldTranslation;
            PlayspaceRoot.Translation = new Vector3(-offset.X, PlayspaceRoot.Translation.Y, -offset.Z);
            PlayspaceRoot.RecalculateMatrices(true);
            Vector3 delta = oldPosition - PlayspaceRoot.WorldTranslation;
            delta.Y = 0.0f;
            movement.AddLiteralInputDelta(delta);
            // Every sampled device shares this playspace. Keep the coherent sample in the new world basis.
            Vector3 worldShift = PlayspaceRoot.WorldTranslation - oldPosition;
            for (int i = 0; i < _sourceSamples.Length; i++)
            {
                if (_sourceSampleValid[i]) _sourceSamples[i].Translation += worldShift;
                if (_estimateSampleValid[i]) _estimatedGoals[i].Translation += worldShift;
            }
        }

        /// <summary>Returns the bind-pose head displacement at the player's explicitly configured scale.</summary>
        public static Vector3 GetScaledToRealWorldHeadOffsetFromAvatarRoot(IHumanoidVrCalibrationRig humanoid)
            => ((humanoid.HeadNode?.Transform.BindMatrix.Translation ?? Vector3.Zero) - humanoid.RootTransform.BindMatrix.Translation)
                * RuntimeVrStateServices.ModelToRealWorldHeightRatio;

        private void DetectDiscontinuity()
        {
            if (PlayerRoot is null) return;
            Vector3 position = PlayerRoot.WorldTranslation;
            Quaternion rotation = PlayspaceRoot?.WorldRotation ?? PlayerRoot.WorldRotation;
            if (Vector3.DistanceSquared(position, _lastPlayerPosition) > 1.0f || MathF.Abs(Quaternion.Dot(rotation, _lastPlayerRotation)) < 0.9961947f)
                NotifyTrackingDiscontinuity();
            _lastPlayerPosition = position;
            _lastPlayerRotation = rotation;
        }

        /// <summary>Resets consumers after teleport, snap turn, avatar replacement, or a known basis change.</summary>
        public void NotifyTrackingDiscontinuity()
        {
            DiscontinuityGeneration++;
            _hasPreviousSample = false;
            BodyPoseSource?.ResetHistory();
            TrackingDiscontinuity?.Invoke();
        }

        /// <summary>An unknown tracking-space relationship cannot safely preserve captured offsets.</summary>
        public void InvalidateTrackingBasis()
        {
            if (IsCalibrating) CancelOnSceneThread();
            HasCalibration = false;
            _headHandsInitialized = false;
            if (_humanoid is not null)
                SessionCalibrations.Remove(_humanoid.SceneNode);
            CalibrationState = VrCalibrationState.Uncalibrated;
            CalibrationMessage = "The tracking origin changed. Reopen calibration.";
            for (int i = 0; i < 11; i++) if (i != 0 && i != 2 && i != 3)
            {
                _boundDevices[i] = null;
                _boundIdentities[i] = null;
                _sourceWeights[i] = 0;
                _rig?.SetSlotWeight((EHumanoidIKTarget)i, 0);
            }
            NotifyTrackingDiscontinuity();
        }
    }
}
