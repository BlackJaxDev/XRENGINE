using Extensions;
using OpenVR.NET.Devices;
using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR
{
    [RequireComponents(typeof(VRHeightScaleComponent))]
    public class VRPlayerCharacterComponent : XRComponent, IRenderable
    {
        public VRPlayerCharacterComponent()
            => RenderedObjects = [RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render))];

        public RenderInfo[] RenderedObjects { get; } = [];

        private void Render()
        {
            if (!IsCalibrating)
                return;

            //var hmd = Headset;
            //if (hmd is not null)
            //{
            //    Vector3 hmdPoint = hmd.WorldTranslation;
            //    Matrix4x4 hmdMtx = hmd.WorldMatrix;

            //    //Clear vertical rotation from the headset matrix
            //    Matrix4x4.Decompose(hmdMtx, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
            //    var rot = Rotator.FromQuaternion(rotation);
            //    rot.Pitch = 0.0f;
            //    rot.Roll = 0.0f;
            //    hmdMtx =
            //        Matrix4x4.CreateScale(scale) * 
            //        Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Globals.Up, float.DegreesToRadians(rot.Yaw))) * 
            //        Matrix4x4.CreateTranslation(translation);

            //    Vector3 headPoint = (Matrix4x4.CreateTranslation(-EyeOffsetFromHead) * hmdMtx).Translation;
            //    Engine.Rendering.Debug.RenderPoint(hmdPoint, ColorF4.Magenta);
            //    Engine.Rendering.Debug.RenderPoint(headPoint, ColorF4.Orange);
            //    Engine.Rendering.Debug.RenderLine(hmdPoint, headPoint, ColorF4.Red);
            //}

            Engine.Rendering.Debug.RenderPoint((Headset?.RenderTranslation ?? Vector3.Zero), ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint((LeftControllerOffset * (LeftController?.RenderMatrix ?? Matrix4x4.Identity)).Translation, ColorF4.Green);
            Engine.Rendering.Debug.RenderPoint((RightControllerOffset * (RightController?.RenderMatrix ?? Matrix4x4.Identity)).Translation, ColorF4.Green);
            var headNode = GetHumanoid()?.Head.Node;
            if (headNode is not null)
            {
                Vector3 headNodePos = headNode.Transform.RenderTranslation;
                Engine.Rendering.Debug.RenderPoint(headNodePos, ColorF4.Yellow);
                Quaternion headNodeRot = Quaternion.Normalize(headNode.Transform.RenderRotation);
                Vector3 scaledToRealWorldEyeOffsetFromHead = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
                Vector3 eyePos = headNodePos + Vector3.Transform(scaledToRealWorldEyeOffsetFromHead, headNodeRot);
                Engine.Rendering.Debug.RenderLine(headNodePos, eyePos, ColorF4.Yellow);

                float halfIpd = Engine.VRState.ScaledIPD * 0.5f;
                Vector3 ipdOffset = Vector3.Transform(new Vector3(halfIpd, 0.0f, 0.0f), headNodeRot);
                Vector3 ipdLeftTrans = eyePos - ipdOffset;
                Vector3 ipdRightTrans = eyePos + ipdOffset;
                Engine.Rendering.Debug.RenderLine(ipdRightTrans, ipdLeftTrans, ColorF4.Magenta);
            }

            //var h = GetHumanoid();
            //var t = GetTrackerCollection();
            //if (h is null || t is null)
            //    return;

            //var waistTfm = h.Hips.Node?.Transform;
            //var leftFootTfm = h.Left.Foot.Node?.Transform;
            //var rightFootTfm = h.Right.Foot.Node?.Transform;

            //var chestTfm = h.Chest.Node?.Transform;
            //var leftElbowTfm = h.Left.Elbow.Node?.Transform;
            //var rightElbowTfm = h.Right.Elbow.Node?.Transform;
            //var leftKneeTfm = h.Left.Knee.Node?.Transform;
            //var rightKneeTfm = h.Right.Knee.Node?.Transform;

            //List<TransformBase?> tfms = [waistTfm, leftFootTfm, rightFootTfm, chestTfm, leftElbowTfm, rightElbowTfm, leftKneeTfm, rightKneeTfm];

            //foreach ((_, VRTrackerTransform tracker) in t.Trackers.Values)
            //{
            //    float bestDist = CalibrationRadius;
            //    int bestIndex = -1;
            //    for (int i = 0; i < tfms.Count; i++)
            //    {
            //        TransformBase? tfm = tfms[i];
            //        if (tfm is null)
            //            continue;
            //        var bodyPos = tfm.WorldTranslation;
            //        var trackerPos = tracker.WorldTranslation;
            //        var dist = Vector3.Distance(bodyPos, trackerPos);
            //        if (dist < bestDist)
            //        {
            //            bestIndex = i;
            //            bestDist = dist;
            //        }
            //    }
            //    if (bestIndex >= 0)
            //    {
            //        var tfm = tfms[bestIndex];
            //        if (tfm is not null)
            //        {
            //            var trackerPos = tracker.WorldTranslation;
            //            Engine.Rendering.Debug.RenderSphere(trackerPos, CalibrationRadius, false, ColorF4.Green);
            //            Engine.Rendering.Debug.RenderLine(trackerPos, tfm.WorldTranslation, ColorF4.Magenta);
            //            Engine.Rendering.Debug.RenderPoint(tfm.WorldTranslation, ColorF4.Green);
            //        }
            //        tfms.RemoveAt(bestIndex);
            //    }
            //}
            //foreach (var tfm in tfms)
            //{
            //    if (tfm is null)
            //        continue;
            //    Engine.Rendering.Debug.RenderPoint(tfm.WorldTranslation, ColorF4.Red);
            //}
        }

        private bool _isCalibrating = false;
        public bool IsCalibrating
        {
            get => _isCalibrating;
            private set => SetField(ref _isCalibrating, value);
        }

        private float _calibrationRadius = 0.25f;
        /// <summary>
        /// The maximum distance from a tracker to a bone for it to be considered a match during calibration.
        /// </summary>
        public float CalibrationRadius
        {
            get => _calibrationRadius;
            set => SetField(ref _calibrationRadius, value);
        }

        private Matrix4x4 _leftControllerOffset = Matrix4x4.Identity;
        /// <summary>
        /// The manually-set offset from the left controller to the left hand bone.
        /// </summary>
        public Matrix4x4 LeftControllerOffset
        {
            get => _leftControllerOffset;
            set => SetField(ref _leftControllerOffset, value);
        }

        private Matrix4x4 _rightControllerOffset = Matrix4x4.Identity;
        /// <summary>
        /// The manually-set offset from the right controller to the right hand bone.
        /// </summary>
        public Matrix4x4 RightControllerOffset
        {
            get => _rightControllerOffset;
            set => SetField(ref _rightControllerOffset, value);
        }

        private HumanoidComponent? _humanoidComponent;
        public HumanoidComponent? HumanoidComponent
        {
            get => _humanoidComponent;
            set => SetField(ref _humanoidComponent, value);
        }

        private VRHeightScaleComponent? _heightScaleComponent;
        public VRHeightScaleComponent? HeightScaleComponent
        {
            get => _heightScaleComponent;
            set => SetField(ref _heightScaleComponent, value);
        }

        private CharacterMovement3DComponent? _characterMovementComponent;
        public CharacterMovement3DComponent? CharacterMovementComponent
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

        private ModelComponent? _eyesModel;
        /// <summary>
        /// Used to calculate floor-to-eye height.
        /// Eye meshes should be rigged to bones that contain the word "eye" in their name.
        /// Or you can set the bone names manually with EyeLBoneName and EyeRBoneName.
        /// </summary>
        public ModelComponent? EyesModel
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

        public HumanoidComponent? GetHumanoid()
            => HumanoidComponent ?? GetSiblingComponent<HumanoidComponent>();
        public VRTrackerCollectionComponent? GetTrackerCollection()
            => TrackerCollection ?? GetSiblingComponent<VRTrackerCollectionComponent>();
        public CharacterMovement3DComponent? GetCharacterMovement()
            => CharacterMovementComponent ?? GetSiblingComponent<CharacterMovement3DComponent>();
        public VRHeightScaleComponent? GetHeightScaleComponent()
            => HeightScaleComponent ?? GetSiblingComponent<VRHeightScaleComponent>();

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

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ResolveDependencies();
            SetInitialState();
            BeginCalibration();
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateTick);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            var h = GetHumanoid();
            if (h is null)
                return;

            h.ClearIKTargets();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateTick);
        }

        private void SetInitialState()
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            GetHeightScaleComponent()?.CalculateEyeOffsetFromHead(EyesModel, EyeLBoneName, EyeRBoneName);

            //Engine.VRState.CalibrationSettings.HeadOffset = ScaledToRealWorldEyeOffsetFromHead;
            h.HeadTarget = (Headset, Matrix4x4.Identity);
            h.LeftHandTarget = (LeftController, Matrix4x4.Identity);
            h.RightHandTarget = (RightController, Matrix4x4.Identity);
            h.HipsTarget = (null, Matrix4x4.Identity);
            h.LeftFootTarget = (null, Matrix4x4.Identity);
            h.RightFootTarget = (null, Matrix4x4.Identity);
            h.ChestTarget = (null, Matrix4x4.Identity);
            h.LeftElbowTarget = (null, Matrix4x4.Identity);
            h.RightElbowTarget = (null, Matrix4x4.Identity);
            h.LeftKneeTarget = (null, Matrix4x4.Identity);
            h.RightKneeTarget = (null, Matrix4x4.Identity);
        }

        private void ResolveDependencies()
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            if (EyesModel is null && EyesModelResolveName is not null)
                EyesModel = h.SceneNode.FindDescendant(x => x.Name?.Contains(EyesModelResolveName, StringComparison.InvariantCultureIgnoreCase) ?? false)?.GetComponent<ModelComponent>();

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

        private static void DebugRenderPlayspace(Transform avatarRootTfm, TransformBase footTfm, Transform playspaceRootTfm)
        {
            Engine.Rendering.Debug.RenderLine(footTfm.WorldTranslation, avatarRootTfm.WorldTranslation, ColorF4.Green);
            Engine.Rendering.Debug.RenderLine(footTfm.WorldTranslation, playspaceRootTfm.WorldTranslation, ColorF4.Orange);
            Engine.Rendering.Debug.RenderLine(avatarRootTfm.WorldTranslation, playspaceRootTfm.WorldTranslation, ColorF4.Red);
        }

        /// <summary>
        /// This will move the playspace opposite the movement delta, and the character movement in the same direction.
        /// </summary>
        /// <param name="playspaceRootTfm"></param>
        /// <param name="movementDelta"></param>
        private void AddMovementInputFromDevice(Transform playspaceRootTfm, Vector3 movementDelta)
        {
            //Move playspace in opposite direction only in XZ
            Vector3 oppMovementOffset = -movementDelta;
            oppMovementOffset.Y = 0.0f;

            var lastPos = playspaceRootTfm.WorldTranslation;
            playspaceRootTfm.Translation = oppMovementOffset;
            playspaceRootTfm.RecalculateMatrices(true);
            var currPos = playspaceRootTfm.WorldTranslation;

            //Move character literally in the same direction as the headset, so opposite the opposite
            var moveDelta = lastPos - currPos;
            float dx = moveDelta.X;
            float dz = moveDelta.Z;

            if (MathF.Abs(dx) < float.Epsilon && MathF.Abs(dz) < float.Epsilon)
                return; //No movement, no need to add input

            GetCharacterMovement()?.AddLiteralInputDelta(new Vector3(dx, 0.0f, dz));
        }

        /// <summary>
        /// Updates the avatar's pose to match the headset's position and rotation.
        /// X and Z translation are cleared because they are used to locomote the avatar elsewhere.
        /// </summary>
        /// <param name="h"></param>
        private void UpdateCalibrationPose(
            HumanoidComponent h,
            Transform avatarRootTfm,
            Transform playspaceRootTfm)
        {
            Matrix4x4 hmdRelativeToFoot = HMDRelativeToPlayspace(h);

            TransformBase.GetDirectionsXZ(hmdRelativeToFoot, out Vector3 forward, out _);

            Matrix4x4 eyePosRot = Matrix4x4.CreateWorld(hmdRelativeToFoot.Translation, forward, Globals.Up);
            Vector3 eyeOffsetFromHead = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
            Matrix4x4 eyeToHead = Matrix4x4.CreateTranslation(-eyeOffsetFromHead);
            Matrix4x4 headToRoot = Matrix4x4.CreateTranslation(-GetScaledToRealWorldHeadOffsetFromAvatarRoot(h));
            Matrix4x4 movementOffset = eyeToHead * eyePosRot;
            Matrix4x4 rootMtx = headToRoot * eyeToHead * eyePosRot;
            Matrix4x4.Decompose(rootMtx, out _, out Quaternion rootRot, out Vector3 rootTrans);

            //Move the avatar root transform to match the headset's rotation and Y translation
            avatarRootTfm.Translation = new Vector3(0.0f, rootTrans.Y, 0.0f);
            avatarRootTfm.Rotation = rootRot;
            avatarRootTfm.RecalculateMatrices(true, true);
            AddMovementInputFromDevice(playspaceRootTfm, movementOffset.Translation);
        }

        private static void GetRelevantMovementTransforms(
            HumanoidComponent h,
            out Transform avatarRootTfm,
            out TransformBase footTfm,
            out Transform playspaceRootTfm)
        {
            avatarRootTfm = h.SceneNode.GetTransformAs<Transform>(true)!;
            footTfm = avatarRootTfm.Parent!;
            playspaceRootTfm = footTfm.FirstChild()!.SceneNode!.GetTransformAs<Transform>(true)!;
        }

        /// <summary>
        /// Moves the playspace and player root to keep the view or hip centered in the playspace and allow the view or hip to move the playspace.
        /// </summary>
        private void MovePlayer(Transform avatarRootTfm, Transform playspaceRootTfm)
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            //Use the hip transform if the tracker controlling it exists and the player isn't calibrating
            bool useHip = h.HipsTarget.tfm is not null;

            CharacterMovement3DComponent? movement = GetCharacterMovement();
            TransformBase? rigidBodyTfm = Transform; //This should be the same transform as the rigid body
            Transform? avatarTfm = h.TransformAs<Transform>(false); //This should be a descendant of the playspace root transform, to control the avatar's position in the playspace
            if (movement is null || rigidBodyTfm is null || avatarTfm is null)
                return;

            Matrix4x4 deviceToBodyOffsetMtx;
            Matrix4x4 deviceMtx;
            if (useHip)
            {
                //If using the hips as the target, we need to follow the hips, not the tracker
                deviceToBodyOffsetMtx = h.HipsTarget.offset;
                deviceMtx = h.HipsTarget.tfm!.WorldMatrix;
            }
            else
            {
                //If using the HMD as the target, we need to follow the eyes
                deviceToBodyOffsetMtx = h.HeadTarget.offset;
                deviceMtx = h.HeadTarget.tfm!.WorldMatrix;
            }

            Matrix4x4 deviceRelativeToFoot = GetTrackedDeviceMatrixRelativeToPlayspace(h, deviceToBodyOffsetMtx * deviceMtx);

            //Avatar root stays centered
            avatarRootTfm.Translation = new Vector3(0.0f, 0.0f, 0.0f);

            TransformBase.GetDirectionsXZ(deviceRelativeToFoot, out Vector3 forward, out _);

            Matrix4x4 headMtx = deviceToBodyOffsetMtx * Matrix4x4.CreateWorld(deviceRelativeToFoot.Translation, forward, Globals.Up);

            AddMovementInputFromDevice(playspaceRootTfm, headMtx.Translation);
        }

        /// <summary>
        /// Called every frame while calibrating to move the avatar into place and to find nearest trackers to body parts.
        /// </summary>
        private void UpdateTick()
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            GetRelevantMovementTransforms(
                h,
                out Transform avatarRootTfm,
                out TransformBase footTfm,
                out Transform playspaceRootTfm);

            if (IsCalibrating)
            {
                _calibrationUpdateFence.Reset();
                UpdateCalibrationPose(h, avatarRootTfm, playspaceRootTfm);
                FindNearestTrackerTargets(h);
                _calibrationUpdateFence.Set();
            }
            else
                MovePlayer(avatarRootTfm, playspaceRootTfm);
            
            DebugRenderPlayspace(avatarRootTfm, footTfm, playspaceRootTfm);
        }

        private static void ClearYTranslation(ref Matrix4x4 hmdRelativeToFoot)
        {
            Vector3 hmdTranslation = hmdRelativeToFoot.Translation;
            hmdTranslation.Y = 0.0f;
            hmdRelativeToFoot.Translation = hmdTranslation;
        }
        //private static void ClearXZTranslation(ref Matrix4x4 hmdRelativeToFoot)
        //{
        //    Vector3 hmdTranslation = hmdRelativeToFoot.Translation;
        //    hmdTranslation.X = 0.0f;
        //    hmdTranslation.Z = 0.0f;
        //    hmdRelativeToFoot.Translation = hmdTranslation;
        //}

        public static Vector3 GetScaledToRealWorldHeadOffsetFromAvatarRoot(HumanoidComponent h)
            => (h.Head.Node!.Transform.BindMatrix.Translation - h.Transform!.BindMatrix.Translation) * Engine.VRState.ModelToRealWorldHeightRatio;

        private static Vector3 GetScaledBodyPartOffsetFromAvatarRoot(HumanoidComponent h, ETrackableBodyPart bodyPart)
        {
            TransformBase? tfm = bodyPart switch
            {
                ETrackableBodyPart.Hips => h.Hips.Node!.Transform,
                ETrackableBodyPart.Chest => h.Chest.Node!.Transform,
                ETrackableBodyPart.LeftFoot => h.Left.Foot.Node!.Transform,
                ETrackableBodyPart.RightFoot => h.Right.Foot.Node!.Transform,
                ETrackableBodyPart.LeftElbow => h.Left.Elbow.Node!.Transform,
                ETrackableBodyPart.RightElbow => h.Right.Elbow.Node!.Transform,
                ETrackableBodyPart.LeftKnee => h.Left.Knee.Node!.Transform,
                ETrackableBodyPart.RightKnee => h.Right.Knee.Node!.Transform,
                _ => null
            };
            return tfm?.WorldMatrix.Translation - h.SceneNode.Transform.WorldMatrix.Translation ?? Vector3.Zero;
        }
        
        private Matrix4x4 HMDRelativeToPlayspace(HumanoidComponent h)
            //=> (Engine.VRState.Api.Headset?.DeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity);
            => GetTrackedDeviceMatrixRelativeToPlayspace(h, Headset?.WorldMatrix ?? Matrix4x4.Identity);

        private static Matrix4x4 GetHipTrackerRelativeToPlayspace(HumanoidComponent h)
            => GetTrackedDeviceMatrixRelativeToPlayspace(h, h.HipsTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);

        private static Matrix4x4 GetTrackedDeviceMatrixRelativeToPlayspace(HumanoidComponent h, Matrix4x4 trackedDeviceMatrix)
        {
            var playspaceTfm = h.SceneNode.Transform.Parent!.FirstChild()!;
            return trackedDeviceMatrix * playspaceTfm.InverseWorldMatrix;
        }

        public bool BeginCalibration()
        {
            if (Headset is null)
                return false;

            var h = GetHumanoid();
            if (h is null)
                return false;

            var headNode = h.Head.Node;
            if (headNode is null)
                return false;

            var trackers = GetTrackerCollection();
            if (trackers is null) //No need to calibrate if there are no trackers
                return false;

            //Save the current targets before they're cleared
            LastHeadTarget = h.HeadTarget;
            LastHipsTarget = h.HipsTarget;
            LastLeftHandTarget = h.LeftHandTarget;
            LastRightHandTarget = h.RightHandTarget;
            LastLeftFootTarget = h.LeftFootTarget;
            LastRightFootTarget = h.RightFootTarget;
            LastChestTarget = h.ChestTarget;
            LastLeftElbowTarget = h.LeftElbowTarget;
            LastRightElbowTarget = h.RightElbowTarget;
            LastLeftKneeTarget = h.LeftKneeTarget;
            LastRightKneeTarget = h.RightKneeTarget;

            //Stop solving
            h.SolveIK = false;
            if (IKSolver is not null)
                IKSolver.IsActive = false;

            //Clear all targets
            h.ClearIKTargets();

            //Reset the pose to T-pose
            h.ResetPose();

            IsCalibrating = true;

            GetHeightScaleComponent()?.MeasureAvatarHeight();

            return true;
        }

        #region Last state for canceling calibration
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

        private VRIKSolverComponent? _ikSolver;
        public VRIKSolverComponent? IKSolver
        {
            get => _ikSolver;
            set => SetField(ref _ikSolver, value);
        }

        #endregion

        private bool EndCalib(out HumanoidComponent? h)
        {
            IsCalibrating = false;

            h = GetHumanoid();
            if (h is null)
                return false;
            
            _calibrationUpdateFence.Wait(100);
            return true;
        }

        public void CancelCalibration()
        {
            if (!EndCalib(out HumanoidComponent? h))
                return;
            
            h!.HeadTarget = LastHeadTarget;
            h.LeftHandTarget = LastLeftHandTarget;
            h.RightHandTarget = LastRightHandTarget;

            h.HipsTarget = LastHipsTarget;
            h.LeftFootTarget = LastLeftFootTarget;
            h.RightFootTarget = LastRightFootTarget;
            h.ChestTarget = LastChestTarget;
            h.LeftElbowTarget = LastLeftElbowTarget;
            h.RightElbowTarget = LastRightElbowTarget;
            h.LeftKneeTarget = LastLeftKneeTarget;
            h.RightKneeTarget = LastRightKneeTarget;

            FinalizeCalib(h);
        }

        public void EndCalibration()
        {
            if (!EndCalib(out HumanoidComponent? h))
                return;

            Vector3 eyeOffsetFromHead = GetHeightScaleComponent()?.ScaledToRealWorldEyeOffsetFromHead ?? Vector3.Zero;
            h!.HeadTarget = (Headset, Matrix4x4.CreateTranslation(-eyeOffsetFromHead));
            h.LeftHandTarget = (LeftController, LeftControllerOffset);
            h.RightHandTarget = (RightController, RightControllerOffset);

            FinalizeCalib(h);
        }

        private void FinalizeCalib(HumanoidComponent h)
        {
            if (IKSolver is null)
            {
                Debug.LogWarning("IKSolver is null, failed to calibrate.");
                return;
            }

            IKSolver.IsActive = true;
            VRIKCalibrator.Calibrate(
                IKSolver,
                Engine.VRState.CalibrationSettings,
                h.HeadTarget.tfm,
                h.HipsTarget.tfm,
                h.LeftHandTarget.tfm,
                h.RightHandTarget.tfm,
                h.LeftFootTarget.tfm,
                h.RightFootTarget.tfm);
        }

        /// <summary>
        /// Body parts trackable by trackers, NOT the HMD or controllers which are always head and hands.
        /// </summary>
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

        private void FindNearestTrackerTargets(HumanoidComponent h)
        {
            var t = GetTrackerCollection();
            if (t is null)
                return;

            var waistTfm = h.Hips.Node?.Transform;
            var leftFootTfm = h.Left.Foot.Node?.Transform;
            var rightFootTfm = h.Right.Foot.Node?.Transform;
            var chestTfm = h.Chest.Node?.Transform;
            var leftElbowTfm = h.Left.Elbow.Node?.Transform;
            var rightElbowTfm = h.Right.Elbow.Node?.Transform;
            var leftKneeTfm = h.Left.Knee.Node?.Transform;
            var rightKneeTfm = h.Right.Knee.Node?.Transform;

            //Clear tracker-dependent targets
            h.HipsTarget = (null, Matrix4x4.Identity);
            h.LeftFootTarget = (null, Matrix4x4.Identity);
            h.RightFootTarget = (null, Matrix4x4.Identity);
            h.ChestTarget = (null, Matrix4x4.Identity);
            h.LeftElbowTarget = (null, Matrix4x4.Identity);
            h.RightElbowTarget = (null, Matrix4x4.Identity);
            h.LeftKneeTarget = (null, Matrix4x4.Identity);
            h.RightKneeTarget = (null, Matrix4x4.Identity);

            foreach ((_, VRTrackerTransform tracker) in t.Trackers.Values)
            {
                //Engine.Rendering.Debug.RenderSphere(tracker.RenderTranslation, CalibrationRadius, false, ColorF4.Green);

                FindClosestBodyPart(
                    tracker,
                    out ETrackableBodyPart closestBodyPart,
                    out float distance,
                    out Matrix4x4 offset2,
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

                Vector3 trackerPos = tracker.RenderTranslation;
                Vector3 humanPosFromTracker = (offset2 * tracker.RenderMatrix).Translation;

                Engine.Rendering.Debug.RenderLine(trackerPos, humanPosFromTracker, ColorF4.Magenta);

                //These two positions should be the same
                Engine.Rendering.Debug.RenderPoint(humanPosFromTracker, ColorF4.Green);

                switch (closestBodyPart)
                {
                    case ETrackableBodyPart.Hips:
                        h.HipsTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(waistTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.Chest:
                        h.ChestTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(chestTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.LeftFoot:
                        h.LeftFootTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(leftFootTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.RightFoot:
                        h.RightFootTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(rightFootTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.LeftElbow:
                        h.LeftElbowTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(leftElbowTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.RightElbow:
                        h.RightElbowTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(rightElbowTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.LeftKnee:
                        h.LeftKneeTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(leftKneeTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                    case ETrackableBodyPart.RightKnee:
                        h.RightKneeTarget = (tracker, offset2);
                        Engine.Rendering.Debug.RenderPoint(rightKneeTfm!.RenderTranslation, ColorF4.Orange);
                        break;
                }
            }
        }

        //protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        //{
        //    base.OnPropertyChanged(propName, prev, field);
        //    switch (propName)
        //    {
        //        case nameof(Headset):
        //            if (Headset is not null)
        //                Headset.WorldMatrixChanged += HeadsetRenderMatrixChanged;
        //            break;
        //    }
        //}

        private static void FindClosestBodyPart(
            VRTrackerTransform tracker,
            out ETrackableBodyPart closestBodyPart,
            out float distance,
            out Matrix4x4 offset2,
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
            offset2 = Matrix4x4.Identity;
            distance = float.MaxValue;
            TransformBase? bestTfm = null;

            if (waistTfm is not null)
            {
                var waistPos = waistTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(waistPos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.Hips;
                    distance = dist;
                    bestTfm = waistTfm;
                }
            }
            if (leftFootTfm is not null)
            {
                var leftFootPos = leftFootTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(leftFootPos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.LeftFoot;
                    distance = dist;
                    bestTfm = leftFootTfm;
                }
            }
            if (rightFootTfm is not null)
            {
                var rightFootPos = rightFootTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(rightFootPos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.RightFoot;
                    distance = dist;
                    bestTfm = rightFootTfm;
                }
            }
            if (chestTfm is not null)
            {
                var chestPos = chestTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(chestPos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.Chest;
                    distance = dist;
                    bestTfm = chestTfm;
                }
            }
            if (leftElbowTfm is not null)
            {
                var leftElbowPos = leftElbowTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(leftElbowPos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.LeftElbow;
                    distance = dist;
                    bestTfm = leftElbowTfm;
                }
            }
            if (rightElbowTfm is not null)
            {
                var rightElbowPos = rightElbowTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(rightElbowPos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.RightElbow;
                    distance = dist;
                    bestTfm = rightElbowTfm;
                }
            }
            if (leftKneeTfm is not null)
            {
                var leftKneePos = leftKneeTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(leftKneePos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.LeftKnee;
                    distance = dist;
                    bestTfm = leftKneeTfm;
                }
            }
            if (rightKneeTfm is not null)
            {
                var rightKneePos = rightKneeTfm.RenderTranslation;
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.Distance(rightKneePos, trackerPos);
                if (dist < distance)
                {
                    closestBodyPart = ETrackableBodyPart.RightKnee;
                    distance = dist;
                    bestTfm = rightKneeTfm;
                }
            }

            if (bestTfm is not null)
                offset2 = bestTfm.RenderMatrix * tracker.RenderMatrix.Inverted();
        }

        private void FindClosestTracker(
            VRTrackerCollectionComponent trackerCollection,
            TransformBase? humanoidTfm,
            out TransformBase? closestTracker,
            out Matrix4x4 offset)
        {
            closestTracker = null;
            offset = Matrix4x4.Identity;

            if (humanoidTfm is null)
                return;

            var bodyPos = humanoidTfm.RenderTranslation;
            float closestDist = float.MaxValue;
            foreach ((VrDevice? dev, VRTrackerTransform tracker) in trackerCollection.Trackers.Values)
            {
                var trackerPos = tracker.RenderTranslation;
                var dist = Vector3.DistanceSquared(bodyPos, trackerPos);
                if (dist < closestDist && float.Sqrt(dist) < CalibrationRadius)
                {
                    closestDist = dist;
                    closestTracker = tracker;
                }
            }
            if (closestTracker is not null)
            {
                offset = humanoidTfm.RenderMatrix * closestTracker.RenderMatrix.Inverted();

                Vector3 trackerPos = closestTracker.RenderTranslation;
                Vector3 humanPosFromTracker = (offset * closestTracker.RenderMatrix).Translation;

                Engine.Rendering.Debug.RenderLine(trackerPos, humanPosFromTracker, ColorF4.Magenta);

                //These two positions should be the same
                Engine.Rendering.Debug.RenderPoint(humanoidTfm.RenderTranslation, ColorF4.Orange);
                Engine.Rendering.Debug.RenderPoint(humanPosFromTracker, ColorF4.Green);
            }
            else
            {
                offset = Matrix4x4.Identity;
            }
        }
    }
}
