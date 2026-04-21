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
    public class VRPlayerCharacterComponent : XRComponent
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
            set => SetField(ref _humanoidComponent, value);
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

        private readonly ManualResetEventSlim _calibrationUpdateFence = new(true);

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ResolveDependencies();
            SetInitialState();
            BeginCalibration();
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateTick);
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            GetHumanoid()?.ClearIKTargets();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateTick);
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

        private static void GetRelevantMovementTransforms(
            IHumanoidVrCalibrationRig humanoid,
            out Transform avatarRootTfm,
            out TransformBase footTfm,
            out Transform playspaceRootTfm)
        {
            avatarRootTfm = humanoid.SceneNode.GetTransformAs<Transform>(true)!;
            footTfm = avatarRootTfm.Parent!;
            playspaceRootTfm = footTfm.FirstChild()!.SceneNode!.GetTransformAs<Transform>(true)!;
        }

        private void MovePlayer(Transform avatarRootTfm, Transform playspaceRootTfm)
        {
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            IVRIKSolverHandle? solver = GetIKSolver();
            if (humanoid is null || solver is null)
                return;

            var hipsTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Hips);
            var headTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Head);
            bool useHip = hipsTarget.tfm is not null;

            IRuntimeCharacterMovementComponent? movement = GetCharacterMovement();
            TransformBase? rigidBodyTransform = Transform;
            Transform? avatarTransform = humanoid.SceneNode.GetTransformAs<Transform>(false);
            if (movement is null || rigidBodyTransform is null || avatarTransform is null)
                return;

            Matrix4x4 deviceToBodyOffsetMatrix;
            Matrix4x4 deviceMatrix;
            if (useHip)
            {
                deviceToBodyOffsetMatrix = hipsTarget.offset;
                deviceMatrix = hipsTarget.tfm!.WorldMatrix;
            }
            else
            {
                deviceToBodyOffsetMatrix = headTarget.offset;
                deviceMatrix = headTarget.tfm!.WorldMatrix;
            }

            Matrix4x4 deviceRelativeToFoot = GetTrackedDeviceMatrixRelativeToPlayspace(humanoid, deviceToBodyOffsetMatrix * deviceMatrix);

            avatarRootTfm.Translation = new Vector3(0.0f, 0.0f, 0.0f);

            TransformBase.GetDirectionsXZ(deviceRelativeToFoot, out Vector3 forward, out _);

            Matrix4x4 headMatrix = deviceToBodyOffsetMatrix * Matrix4x4.CreateWorld(deviceRelativeToFoot.Translation, forward, Globals.Up);

            AddMovementInputFromDevice(playspaceRootTfm, headMatrix.Translation);
        }

        private void UpdateTick()
        {
            IHumanoidVrCalibrationRig? humanoid = GetHumanoid();
            if (humanoid is null)
                return;

            GetRelevantMovementTransforms(
                humanoid,
                out Transform avatarRootTfm,
                out TransformBase footTfm,
                out Transform playspaceRootTfm);

            if (IsCalibrating)
            {
                _calibrationUpdateFence.Reset();
                UpdateCalibrationPose(humanoid, avatarRootTfm, playspaceRootTfm);
                if (GetIKSolver() is not null)
                    FindNearestTrackerTargets(humanoid);
                _calibrationUpdateFence.Set();
            }
            else
                MovePlayer(avatarRootTfm, playspaceRootTfm);
        }

        public static Vector3 GetScaledToRealWorldHeadOffsetFromAvatarRoot(IHumanoidVrCalibrationRig humanoid)
            => (humanoid.HeadNode!.Transform.BindMatrix.Translation - humanoid.RootTransform.BindMatrix.Translation) * RuntimeVrStateServices.ModelToRealWorldHeightRatio;

        private Matrix4x4 HMDRelativeToPlayspace(IHumanoidVrCalibrationRig humanoid)
            => GetTrackedDeviceMatrixRelativeToPlayspace(humanoid, Headset?.WorldMatrix ?? Matrix4x4.Identity);

        private static Matrix4x4 GetTrackedDeviceMatrixRelativeToPlayspace(IHumanoidVrCalibrationRig humanoid, Matrix4x4 trackedDeviceMatrix)
        {
            TransformBase playspaceTransform = humanoid.SceneNode.Transform.Parent!.FirstChild()!;
            return trackedDeviceMatrix * playspaceTransform.InverseWorldMatrix;
        }

        public bool BeginCalibration()
        {
            if (Headset is null)
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

            solver.IsActive = false;
            humanoid.ClearIKTargets();
            humanoid.ResetPose();
            IsCalibrating = true;
            GetHeightScaleComponent()?.MeasureAvatarHeight();

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

        private bool EndCalib(out IHumanoidVrCalibrationRig? humanoid, out IVRIKSolverHandle? solver)
        {
            IsCalibrating = false;

            humanoid = GetHumanoid();
            solver = GetIKSolver();
            if (humanoid is null || solver is null)
                return false;

            _calibrationUpdateFence.Wait(100);
            return true;
        }

        public void CancelCalibration()
        {
            if (!EndCalib(out IHumanoidVrCalibrationRig? humanoid, out IVRIKSolverHandle? solver))
                return;

            RestoreTargets(humanoid!, LastHeadTarget, LastHipsTarget, LastLeftHandTarget, LastRightHandTarget, LastLeftFootTarget, LastRightFootTarget, LastChestTarget, LastLeftElbowTarget, LastRightElbowTarget, LastLeftKneeTarget, LastRightKneeTarget);
            FinalizeCalib(humanoid!, solver!);
        }

        public void EndCalibration()
        {
            if (!EndCalib(out IHumanoidVrCalibrationRig? humanoid, out IVRIKSolverHandle? solver))
                return;

            Vector3 eyeOffsetFromHead = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
            humanoid!.SetIKTarget(EHumanoidIKTarget.Head, Headset, Matrix4x4.CreateTranslation(-eyeOffsetFromHead));
            humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, LeftController, LeftControllerOffset);
            humanoid.SetIKTarget(EHumanoidIKTarget.RightHand, RightController, RightControllerOffset);

            FinalizeCalib(humanoid, solver!);
        }

        private void FinalizeCalib(IHumanoidVrCalibrationRig humanoid, IVRIKSolverHandle solver)
        {
            solver.IsActive = true;
            var headTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Head);
            var hipsTarget = humanoid.GetIKTarget(EHumanoidIKTarget.Hips);
            var leftHandTarget = humanoid.GetIKTarget(EHumanoidIKTarget.LeftHand);
            var rightHandTarget = humanoid.GetIKTarget(EHumanoidIKTarget.RightHand);
            var leftFootTarget = humanoid.GetIKTarget(EHumanoidIKTarget.LeftFoot);
            var rightFootTarget = humanoid.GetIKTarget(EHumanoidIKTarget.RightFoot);

            RuntimeVRIKCalibrator.Calibrate(
                solver,
                RuntimeVrStateServices.CalibrationSettings,
                headTarget.tfm,
                hipsTarget.tfm,
                leftHandTarget.tfm,
                rightHandTarget.tfm,
                leftFootTarget.tfm,
                rightFootTarget.tfm);
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

        private void FindNearestTrackerTargets(IHumanoidVrCalibrationRig humanoid)
        {
            VRTrackerCollectionComponent? trackers = GetTrackerCollection();
            if (trackers is null)
                return;

            TransformBase? waistTfm = humanoid.HipsNode?.Transform;
            TransformBase? leftFootTfm = humanoid.LeftFootNode?.Transform;
            TransformBase? rightFootTfm = humanoid.RightFootNode?.Transform;
            TransformBase? chestTfm = humanoid.ChestNode?.Transform;
            TransformBase? leftElbowTfm = humanoid.LeftElbowNode?.Transform;
            TransformBase? rightElbowTfm = humanoid.RightElbowNode?.Transform;
            TransformBase? leftKneeTfm = humanoid.LeftKneeNode?.Transform;
            TransformBase? rightKneeTfm = humanoid.RightKneeNode?.Transform;

            ClearTrackerTargets(humanoid);

            foreach ((_, VRTrackerTransform tracker) in trackers.Trackers.Values)
            {
                FindClosestBodyPart(
                    tracker,
                    out ETrackableBodyPart closestBodyPart,
                    out float distance,
                    out Matrix4x4 offset,
                    waistTfm,
                    leftFootTfm,
                    rightFootTfm,
                    chestTfm,
                    leftElbowTfm,
                    rightElbowTfm,
                    leftKneeTfm,
                    rightKneeTfm);

                if (distance > CalibrationRadius)
                    continue;

                switch (closestBodyPart)
                {
                    case ETrackableBodyPart.Hips:
                        humanoid.SetIKTarget(EHumanoidIKTarget.Hips, tracker, offset);
                        break;
                    case ETrackableBodyPart.Chest:
                        humanoid.SetIKTarget(EHumanoidIKTarget.Chest, tracker, offset);
                        break;
                    case ETrackableBodyPart.LeftFoot:
                        humanoid.SetIKTarget(EHumanoidIKTarget.LeftFoot, tracker, offset);
                        break;
                    case ETrackableBodyPart.RightFoot:
                        humanoid.SetIKTarget(EHumanoidIKTarget.RightFoot, tracker, offset);
                        break;
                    case ETrackableBodyPart.LeftElbow:
                        humanoid.SetIKTarget(EHumanoidIKTarget.LeftElbow, tracker, offset);
                        break;
                    case ETrackableBodyPart.RightElbow:
                        humanoid.SetIKTarget(EHumanoidIKTarget.RightElbow, tracker, offset);
                        break;
                    case ETrackableBodyPart.LeftKnee:
                        humanoid.SetIKTarget(EHumanoidIKTarget.LeftKnee, tracker, offset);
                        break;
                    case ETrackableBodyPart.RightKnee:
                        humanoid.SetIKTarget(EHumanoidIKTarget.RightKnee, tracker, offset);
                        break;
                }
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

        private static void FindClosestBodyPart(
            VRTrackerTransform tracker,
            out ETrackableBodyPart closestBodyPart,
            out float distance,
            out Matrix4x4 offset,
            TransformBase? waistTfm,
            TransformBase? leftFootTfm,
            TransformBase? rightFootTfm,
            TransformBase? chestTfm,
            TransformBase? leftElbowTfm,
            TransformBase? rightElbowTfm,
            TransformBase? leftKneeTfm,
            TransformBase? rightKneeTfm)
        {
            closestBodyPart = ETrackableBodyPart.Hips;
            offset = Matrix4x4.Identity;
            distance = float.MaxValue;
            TransformBase? bestTransform = null;

            if (waistTfm is not null)
            {
                float testDistance = Vector3.Distance(waistTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.Hips;
                    distance = testDistance;
                    bestTransform = waistTfm;
                }
            }

            if (leftFootTfm is not null)
            {
                float testDistance = Vector3.Distance(leftFootTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.LeftFoot;
                    distance = testDistance;
                    bestTransform = leftFootTfm;
                }
            }

            if (rightFootTfm is not null)
            {
                float testDistance = Vector3.Distance(rightFootTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.RightFoot;
                    distance = testDistance;
                    bestTransform = rightFootTfm;
                }
            }

            if (chestTfm is not null)
            {
                float testDistance = Vector3.Distance(chestTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.Chest;
                    distance = testDistance;
                    bestTransform = chestTfm;
                }
            }

            if (leftElbowTfm is not null)
            {
                float testDistance = Vector3.Distance(leftElbowTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.LeftElbow;
                    distance = testDistance;
                    bestTransform = leftElbowTfm;
                }
            }

            if (rightElbowTfm is not null)
            {
                float testDistance = Vector3.Distance(rightElbowTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.RightElbow;
                    distance = testDistance;
                    bestTransform = rightElbowTfm;
                }
            }

            if (leftKneeTfm is not null)
            {
                float testDistance = Vector3.Distance(leftKneeTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.LeftKnee;
                    distance = testDistance;
                    bestTransform = leftKneeTfm;
                }
            }

            if (rightKneeTfm is not null)
            {
                float testDistance = Vector3.Distance(rightKneeTfm.RenderTranslation, tracker.RenderTranslation);
                if (testDistance < distance)
                {
                    closestBodyPart = ETrackableBodyPart.RightKnee;
                    distance = testDistance;
                    bestTransform = rightKneeTfm;
                }
            }

            if (bestTransform is not null)
                offset = bestTransform.RenderMatrix * tracker.RenderMatrix.Inverted();
        }

        private void FindClosestTracker(
            VRTrackerCollectionComponent trackerCollection,
            TransformBase? humanoidTransform,
            out TransformBase? closestTracker,
            out Matrix4x4 offset)
        {
            closestTracker = null;
            offset = Matrix4x4.Identity;

            if (humanoidTransform is null)
                return;

            Vector3 bodyPosition = humanoidTransform.RenderTranslation;
            float closestDistance = float.MaxValue;
            foreach ((VrDevice? _, VRTrackerTransform tracker) in trackerCollection.Trackers.Values)
            {
                float distanceSquared = Vector3.DistanceSquared(bodyPosition, tracker.RenderTranslation);
                if (distanceSquared < closestDistance && float.Sqrt(distanceSquared) < CalibrationRadius)
                {
                    closestDistance = distanceSquared;
                    closestTracker = tracker;
                }
            }

            if (closestTracker is not null)
                offset = humanoidTransform.RenderMatrix * closestTracker.RenderMatrix.Inverted();
        }
    }
}