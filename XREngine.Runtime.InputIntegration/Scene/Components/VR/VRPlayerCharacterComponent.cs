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
                if (ReferenceEquals(_humanoidComponent, value))
                    return;
                if (_humanoidComponent is not null)
                    PrepareForAvatarReplacement();
                SetField(ref _humanoidComponent, value);
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



        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ResolveDependencies();
            _attemptedSessionRestore = false;
            HasCommittedCalibration = false;
            CalibrationState = EVrCalibrationState.Uncalibrated;
            SetInitialState();
            RegisterCalibrationInput(false);
            RegisterMovementDiscontinuities(false);
            RegisterTick(ETickGroup.Normal, (ETickOrder)((int)ETickOrder.Input + 1), UpdateTick);
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            RegisterCalibrationInput(true);
            RegisterMovementDiscontinuities(true);
            ReleaseSimulationSnapshot();
            RestoreSpectatorDesktop();
            if (IsCalibrating)
                CancelCalibrationImmediate();
            GetHumanoid()?.ClearIKTargets();
            UnregisterTick(ETickGroup.Normal, (ETickOrder)((int)ETickOrder.Input + 1), UpdateTick);
        }

        public void InitializeRig()
        {
            ResolveDependencies();
            RegisterMovementDiscontinuities(false);
            if (!HasCommittedCalibration)
                SetInitialState();
        }

        private void SetInitialState()
        {
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            IVRIKSolverHandle? solver = GetIKSolver();
            if (humanoid is null || solver is null)
                return;

            GetHeightScaleComponent()?.CalculateEyeOffsetFromHead(EyesModel, EyeLBoneName, EyeRBoneName);

            humanoid.SetIKTarget(EHumanoidIKTarget.Head, Headset, Matrix4x4.Identity);
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, LeftController, Matrix4x4.Identity);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightHand, RightController, Matrix4x4.Identity);
            ClearTrackerTargets(humanoid);
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

        private float _crouchedHeightRatio = 0.5f;
        public float CrouchedHeightRatio
        {
            get => _crouchedHeightRatio;
            set => SetField(ref _crouchedHeightRatio, value);
        }

        private float _proneHeightRatio = 0.2f;
        public float ProneHeightRatio
        {
            get => _proneHeightRatio;
            set => SetField(ref _proneHeightRatio, value);
        }

        private float _radiusRatio = 0.2f;
        public float RadiusRatio
        {
            get => _radiusRatio;
            set => SetField(ref _radiusRatio, value);
        }

        private void AddMovementInputFromDevice(Transform playspaceRootTfm, Vector3 movementDelta)
        {
            Vector3 oppositeMovementOffset = -movementDelta;
            oppositeMovementOffset.Y = 0.0f;

            Vector3 lastPosition = playspaceRootTfm.WorldTranslation;
            playspaceRootTfm.Translation = oppositeMovementOffset;
            playspaceRootTfm.RecalculateMatrices(true);
            Vector3 currentPosition = playspaceRootTfm.WorldTranslation;

            Vector3 moveDelta = lastPosition - currentPosition;
            float dx = moveDelta.X;
            float dz = moveDelta.Z;

            if (MathF.Abs(dx) < float.Epsilon && MathF.Abs(dz) < float.Epsilon)
                return;

            GetCharacterMovement()?.AddLiteralInputDelta(new Vector3(dx, 0.0f, dz));
        }

        private void UpdateCalibrationPose(
            IHumanoidVrCalibrationRig humanoid,
            Transform avatarRootTfm,
            Transform playspaceRootTfm)
        {
            Matrix4x4 hmdRelativeToFoot = HMDRelativeToPlayspace(humanoid);

            TransformBase.GetDirectionsXZ(hmdRelativeToFoot, out Vector3 forward, out _);

            Matrix4x4 eyePositionRotation = Matrix4x4.CreateWorld(hmdRelativeToFoot.Translation, forward, Globals.Up);
            Vector3 eyeOffsetFromHead = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
            Matrix4x4 eyeToHead = Matrix4x4.CreateTranslation(-eyeOffsetFromHead);
            Matrix4x4 headToRoot = Matrix4x4.CreateTranslation(-GetScaledToRealWorldHeadOffsetFromAvatarRoot(humanoid));
            Matrix4x4 movementOffset = eyeToHead * eyePositionRotation;
            Matrix4x4 rootMatrix = headToRoot * eyeToHead * eyePositionRotation;
            Matrix4x4.Decompose(rootMatrix, out _, out Quaternion rootRotation, out Vector3 rootTranslation);

            avatarRootTfm.Translation = new Vector3(0.0f, rootTranslation.Y, 0.0f);
            avatarRootTfm.Rotation = rootRotation;
            avatarRootTfm.RecalculateMatrices(true, false);
            AddMovementInputFromDevice(playspaceRootTfm, movementOffset.Translation);
        }

        /// <summary>The explicit metric tracking basis. Device parent is only the compatibility default.</summary>
        public Transform? PlayspaceRoot { get; set; }

        private void MovePlayer(Transform avatarRootTfm, Transform playspaceRootTfm)
        {
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            IVRIKSolverHandle? solver = GetIKSolver();
            if (humanoid is null || solver is null)
                return;

            var hipsTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Hips);
            var headTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Head);
            TransformBase? resolvedHips = solver.GetCalibratedTarget(EHumanoidIKTarget.Hips);
            TransformBase? resolvedHead = solver.GetCalibratedTarget(EHumanoidIKTarget.Head) ?? headTarget.tfm;
            // Retained target transforms support IK loss blending, not room-scale movement.
            bool useHip = resolvedHips is not null
                && (hipsTarget.tfm is not IVrTrackingPoseSource hipsPose || hipsPose.PoseCurrentlyUsable);
            bool useHead = resolvedHead is not null
                && (headTarget.tfm is not IVrTrackingPoseSource headPose || headPose.PoseCurrentlyUsable);
            if (!useHip && !useHead)
                return;

            IRuntimeCharacterMovementComponent? movement = GetCharacterMovement();
            TransformBase? rigidBodyTransform = Transform;
            Transform? avatarTransform = humanoid.SceneNode.GetTransformAs<Transform>(false);
            if (movement is null || rigidBodyTransform is null || avatarTransform is null)
                return;

            (useHip ? resolvedHips! : resolvedHead!).RecalculateMatrices(forceWorldRecalc: true);
            Matrix4x4 deviceToBodyOffsetMatrix;
            Matrix4x4 deviceMatrix;
            if (useHip)
            {
                deviceToBodyOffsetMatrix = Matrix4x4.Identity;
                deviceMatrix = resolvedHips!.WorldMatrix;
            }
            else
            {
                deviceToBodyOffsetMatrix = Matrix4x4.Identity;
                deviceMatrix = resolvedHead!.WorldMatrix;
            }

            Matrix4x4 deviceRelativeToFoot = GetTrackedDeviceMatrixRelativeToPlayspace(humanoid, deviceToBodyOffsetMatrix * deviceMatrix);

            avatarRootTfm.Translation = new Vector3(0.0f, 0.0f, 0.0f);

            TransformBase.GetDirectionsXZ(deviceRelativeToFoot, out Vector3 forward, out _);

            Matrix4x4 headMatrix = Matrix4x4.CreateWorld(deviceRelativeToFoot.Translation, forward, Globals.Up);

            AddMovementInputFromDevice(playspaceRootTfm, headMatrix.Translation);
        }

        private void UpdateTick()
        {
            PublishSimulationSnapshot();
            ProcessCalibrationRequest();
            EnsureInitialTrackingRig();
            Spectator?.UpdateFirstPersonVisibility(IsCalibrating);
            ApplySpectatorDesktopRouting();
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            if (humanoid is null)
                return;

            if (humanoid.SceneNode.Transform is not Transform avatarRootTfm
                || (PlayspaceRoot ?? Headset?.Parent) is not Transform playspaceRootTfm)
                return;

            if (IsCalibrating)
            {
                UpdateCalibrationPose(humanoid, avatarRootTfm, playspaceRootTfm);
                if (GetIKSolver() is not null)
                    FindNearestTrackerTargets(humanoid);
                SampleCalibrationWindow();
            }
            else
                MovePlayer(avatarRootTfm, playspaceRootTfm);
        }

        private static Matrix4x4 GetFixedEyeToHeadOffset(IHumanoidVrCalibrationRig humanoid, Vector3 scaledEyeOffset)
        {
            Matrix4x4 relative = (humanoid.HeadNode?.Transform.BindMatrix ?? Matrix4x4.Identity) * humanoid.RootTransform.InverseBindMatrix;
            if (!VrCalibrationMath.TryGetRigidPose(relative, out Matrix4x4 offset))
                offset = Matrix4x4.Identity;
            offset.Translation = -Vector3.TransformNormal(scaledEyeOffset, offset);
            return offset;
        }

        public static Vector3 GetScaledToRealWorldHeadOffsetFromAvatarRoot(IHumanoidVrCalibrationRig humanoid)
            => (humanoid.HeadNode!.Transform.BindMatrix.Translation - humanoid.RootTransform.BindMatrix.Translation) * humanoid.RootTransform.LossyWorldScale.Y;

        private Matrix4x4 HMDRelativeToPlayspace(IHumanoidVrCalibrationRig humanoid)
            => GetTrackedDeviceMatrixRelativeToPlayspace(humanoid, Headset?.WorldMatrix ?? Matrix4x4.Identity);

        private Matrix4x4 GetTrackedDeviceMatrixRelativeToPlayspace(IHumanoidVrCalibrationRig humanoid, Matrix4x4 trackedDeviceMatrix)
        {
            TransformBase? playspaceTransform = PlayspaceRoot ?? Headset?.Parent;
            return trackedDeviceMatrix * (playspaceTransform?.InverseWorldMatrix ?? Matrix4x4.Identity);
        }

        private bool BeginCalibrationImmediate()
        {
            if (IsCalibrating || Headset is null)
                return false;

            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            IVRIKSolverHandle? solver = GetIKSolver();
            if (humanoid is null || solver is null)
                return false;

            if (humanoid.HeadNode is null)
                return false;

            VRTrackerCollectionComponent? trackers = GetTrackerCollection();
            if (trackers is null)
                return false;
            SaveCalibrationState(humanoid, solver);
            IRuntimeVrHeightScaleComponent? scale = GetHeightScaleComponent();
            string notice = "Avatar measurement settings are unavailable.";
            if (scale is null || !scale.TryApplyPlayerMeasurements(out notice))
            {
                RestoreCalibrationState(humanoid, solver);
                CalibrationMessage = scale is null ? "Avatar measurement settings are unavailable." : notice;
                return false;
            }


            solver.IsActive = false;
            humanoid.ClearIKTargets();
            humanoid.PosePreviewMode = EHumanoidPosePreviewMode.TPose;
            if (!humanoid.SetCanonicalCalibrationPose())
            {
                RestoreCalibrationState(humanoid, solver);
                CalibrationMessage = "The avatar cannot form a canonical T-pose.";
                return false;
            }
            IsCalibrating = true;
            CalibrationState = EVrCalibrationState.Calibrating;
            CalibrationMessage = "Stand straight in the footprints, look ahead, then pull both triggers. Cancel to keep your previous calibration.";
            _stationaryWindow.Reset();
            _previousSnapshot = default;

            return true;
        }

        public (TransformBase? tfm, Matrix4x4 offset) LastHeadTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastHipsTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastLeftHandTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastRightHandTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastLeftFootTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastRightFootTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastLeftElbowTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastRightElbowTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastLeftKneeTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastRightKneeTarget { get; set; } = (null, Matrix4x4.Identity);
        public (TransformBase? tfm, Matrix4x4 offset) LastChestTarget { get; set; } = (null, Matrix4x4.Identity);

        private XRComponent? _ikSolver;
        public XRComponent? IKSolver
        {
            get => _ikSolver;
            set => SetField(ref _ikSolver, value);
        }

        private void CancelCalibrationImmediate()
        {
            if (!IsCalibrating)
                return;
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            IVRIKSolverHandle? solver = GetIKSolver();
            if (humanoid is null || solver is null)
                return;
            RestoreCalibrationState(humanoid, solver);
            IsCalibrating = false;
            CalibrationState = _previousCalibrationState;
            CalibrationMessage = "Calibration canceled; previous rig restored.";
        }

        private void CaptureCalibrationImmediate()
        {
            if (!IsCalibrating || GetHumanoid() is not { } humanoid || GetIKSolver() is not { } solver)
                return;
            if (!TryBuildCaptureSnapshot(humanoid, out VrCalibrationPose[] poses))
                return;
            Vector3 eyeOffset = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
            humanoid.SetIKTarget(EHumanoidIKTarget.Head, Headset, GetFixedEyeToHeadOffset(humanoid, eyeOffset));
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, LeftController, LeftControllerOffset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightHand, RightController, RightControllerOffset);
            VrCalibrationResult result = RuntimeVRIKCalibrator.CalibrateSnapshot(solver, RuntimeVrStateServices.CalibrationSettings, poses);
            if (!result.Success)
            {
                RestoreCalibrationState(humanoid, solver);
                IsCalibrating = false;
                CalibrationState = EVrCalibrationState.Failed;
                CalibrationMessage = result.Message + " Your previous rig is unchanged.";
                return;
            }
            humanoid.PosePreviewMode = _previousPreviewMode;
            solver.IsActive = true;
            IsCalibrating = false;
            HasCommittedCalibration = true;
            CalibrationState = EVrCalibrationState.Calibrated;
            CalibrationMessage = GetHeightScaleComponent()?.GetCaptureMeasurementWarning(_previousSnapshot.HeadPose.Translation.Y)
                ?? "Calibration complete.";
            SaveCommittedSession();
            _savedPose.Clear();
        }

        public enum ETrackableBodyPart
        {
            Hips,
            Chest,
            LeftFoot,
            RightFoot,
            LeftElbow,
            RightElbow,
            LeftKnee,
            RightKnee,
        }

        private VrTrackerBindingCandidate[] _bindingCandidates = new VrTrackerBindingCandidate[16];
        private VRTrackerTransform?[] _bindingDevices = new VRTrackerTransform?[16];
        private readonly VrTrackerBindingSlot[] _bindingSlots = new VrTrackerBindingSlot[8];
        private readonly VrTrackerBinding[] _previewBindings = new VrTrackerBinding[8];

        private void FindNearestTrackerTargets(IHumanoidVrCalibrationRig humanoid)
        {
            VRTrackerCollectionComponent? trackers = GetTrackerCollection();
            if (trackers is null)
                return;
            int trackerCount = trackers.Trackers.Count;
            // Discovery can grow this calibration-only workspace; gameplay never performs assignment.
            if (_bindingCandidates.Length < trackerCount)
            {
                Array.Resize(ref _bindingCandidates, trackerCount);
                Array.Resize(ref _bindingDevices, trackerCount);
            }
            int candidateCount = 0;
            foreach (var pair in trackers.Trackers.Values)
            {
                VRTrackerTransform tracker = pair.Item2;
                _bindingDevices[candidateCount] = tracker;
                _bindingCandidates[candidateCount++] = new(tracker.TrackingIdentity ?? string.Empty,
                    tracker.WorldTranslation, tracker.PoseCurrentlyUsable);
            }
            int slotCount = 0;
            AddBindingSlot(EHumanoidIKTarget.Hips, humanoid.HipsNode?.Transform);
            AddBindingSlot(EHumanoidIKTarget.Chest, humanoid.ChestNode?.Transform);
            AddBindingSlot(EHumanoidIKTarget.LeftFoot, humanoid.LeftFootNode?.Transform);
            AddBindingSlot(EHumanoidIKTarget.RightFoot, humanoid.RightFootNode?.Transform);
            AddBindingSlot(EHumanoidIKTarget.LeftElbow, humanoid.LeftElbowNode?.Transform, true);
            AddBindingSlot(EHumanoidIKTarget.RightElbow, humanoid.RightElbowNode?.Transform, true);
            AddBindingSlot(EHumanoidIKTarget.LeftKnee, humanoid.LeftKneeNode?.Transform, true, true);
            AddBindingSlot(EHumanoidIKTarget.RightKnee, humanoid.RightKneeNode?.Transform, true, true);
            float scale = MathF.Abs(humanoid.RootTransform.LossyWorldScale.Y);
            int count = VrTrackerBinder.Bind(_bindingSlots.AsSpan(0, slotCount), _bindingCandidates.AsSpan(0, candidateCount),
                CalibrationRadius * scale, _previewBindings);
            ClearTrackerTargets(humanoid);
            for (int i = 0; i < count; i++)
            {
                VrTrackerBinding binding = _previewBindings[i];
                humanoid.SetIKTarget(_bindingSlots[binding.SlotIndex].Slot, _bindingDevices[binding.TrackerIndex], Matrix4x4.Identity);
            }

            void AddBindingSlot(EHumanoidIKTarget slot, TransformBase? bone, bool segment = false, bool knee = false)
            {
                if (bone is null)
                    return;
                Vector3 start = bone.WorldTranslation, end = start;
                if (segment && bone.Parent is { } parent)
                {
                    start = parent.WorldTranslation;
                    end = bone.WorldTranslation;
                    if (knee)
                    {
                        start = Vector3.Lerp(start, end, 0.5f);
                        if (bone.ChildCount > 0 && bone.GetChild(0) is { } child)
                            end = Vector3.Lerp(end, child.WorldTranslation, 0.5f);
                    }
                }
                _bindingSlots[slotCount++] = new(slot, start, end);
            }
        }

        private static void ClearTrackerTargets(IHumanoidVrCalibrationRig humanoid)
        {
            humanoid.ClearIKTarget(EHumanoidIKTarget.Hips);
            humanoid.ClearIKTarget(EHumanoidIKTarget.LeftFoot);
            humanoid.ClearIKTarget(EHumanoidIKTarget.RightFoot);
            humanoid.ClearIKTarget(EHumanoidIKTarget.Chest);
            humanoid.ClearIKTarget(EHumanoidIKTarget.LeftElbow);
            humanoid.ClearIKTarget(EHumanoidIKTarget.RightElbow);
            humanoid.ClearIKTarget(EHumanoidIKTarget.LeftKnee);
            humanoid.ClearIKTarget(EHumanoidIKTarget.RightKnee);
        }

        private static void RestoreTargets(
            IHumanoidVrCalibrationRig humanoid,
            (TransformBase? tfm, Matrix4x4 offset) head,
            (TransformBase? tfm, Matrix4x4 offset) hips,
            (TransformBase? tfm, Matrix4x4 offset) leftHand,
            (TransformBase? tfm, Matrix4x4 offset) rightHand,
            (TransformBase? tfm, Matrix4x4 offset) leftFoot,
            (TransformBase? tfm, Matrix4x4 offset) rightFoot,
            (TransformBase? tfm, Matrix4x4 offset) chest,
            (TransformBase? tfm, Matrix4x4 offset) leftElbow,
            (TransformBase? tfm, Matrix4x4 offset) rightElbow,
            (TransformBase? tfm, Matrix4x4 offset) leftKnee,
            (TransformBase? tfm, Matrix4x4 offset) rightKnee)
        {
            humanoid.SetIKTarget(EHumanoidIKTarget.Head, head.tfm, head.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.Hips, hips.tfm, hips.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, leftHand.tfm, leftHand.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightHand, rightHand.tfm, rightHand.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftFoot, leftFoot.tfm, leftFoot.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightFoot, rightFoot.tfm, rightFoot.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.Chest, chest.tfm, chest.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftElbow, leftElbow.tfm, leftElbow.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightElbow, rightElbow.tfm, rightElbow.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftKnee, leftKnee.tfm, leftKnee.offset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightKnee, rightKnee.tfm, rightKnee.offset);
        }

    }
}
