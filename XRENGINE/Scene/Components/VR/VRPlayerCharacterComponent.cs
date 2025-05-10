using OpenVR.NET.Devices;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Components.Scene;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.VR
{
    public class VRPlayerCharacterComponent : XRComponent, IRenderable
    {
        public VRPlayerCharacterComponent()
            => RenderedObjects = [RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render))];

        public RenderInfo[] RenderedObjects { get; } = [];

        private void Render()
        {
            if (!IsCalibrating)
                return;

            var hmd = Headset;
            if (hmd is not null)
            {
                Vector3 hmdPoint = hmd.WorldTranslation;
                Matrix4x4 hmdMtx = hmd.WorldMatrix;

                //Clear vertical rotation from the headset matrix
                Matrix4x4.Decompose(hmdMtx, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
                var rot = Rotator.FromQuaternion(rotation);
                rot.Pitch = 0.0f;
                rot.Roll = 0.0f;
                hmdMtx =
                    Matrix4x4.CreateScale(scale) * 
                    Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Globals.Up, float.DegreesToRadians(rot.Yaw))) * 
                    Matrix4x4.CreateTranslation(translation);

                Vector3 headPoint = (Matrix4x4.CreateTranslation(-EyeOffsetFromHead) * hmdMtx).Translation;
                Engine.Rendering.Debug.RenderPoint(hmdPoint, ColorF4.Magenta);
                Engine.Rendering.Debug.RenderPoint(headPoint, ColorF4.Orange);
                Engine.Rendering.Debug.RenderLine(hmdPoint, headPoint, ColorF4.Red);
            }

            var leftHand = (LeftController, LeftControllerOffset);
            Engine.Rendering.Debug.RenderPoint((leftHand.LeftController?.WorldTranslation ?? Vector3.Zero) + LeftControllerOffset.Translation, ColorF4.Green);

            var rightHand = (RightController, RightControllerOffset);
            Engine.Rendering.Debug.RenderPoint((rightHand.RightController?.WorldTranslation ?? Vector3.Zero) + RightControllerOffset.Translation, ColorF4.Green);

            var h = GetHumanoid();
            var t = GetTrackerCollection();
            if (h is null || t is null)
                return;
            
            var waistTfm = h.Hips.Node?.Transform;
            var leftFootTfm = h.Left.Foot.Node?.Transform;
            var rightFootTfm = h.Right.Foot.Node?.Transform;

            var chestTfm = h.Chest.Node?.Transform;
            var leftElbowTfm = h.Left.Elbow.Node?.Transform;
            var rightElbowTfm = h.Right.Elbow.Node?.Transform;
            var leftKneeTfm = h.Left.Knee.Node?.Transform;
            var rightKneeTfm = h.Right.Knee.Node?.Transform;

            List<TransformBase?> tfms = [waistTfm, leftFootTfm, rightFootTfm, chestTfm, leftElbowTfm, rightElbowTfm, leftKneeTfm, rightKneeTfm];

            foreach ((_, VRTrackerTransform tracker) in t.Trackers.Values)
            {
                float bestDist = CalibrationRadius;
                int bestIndex = -1;
                for (int i = 0; i < tfms.Count; i++)
                {
                    TransformBase? tfm = tfms[i];
                    if (tfm is null)
                        continue;
                    var bodyPos = tfm.WorldTranslation;
                    var trackerPos = tracker.WorldTranslation;
                    var dist = Vector3.Distance(bodyPos, trackerPos);
                    if (dist < bestDist)
                    {
                        bestIndex = i;
                        bestDist = dist;
                    }
                }
                if (bestIndex >= 0)
                {
                    var tfm = tfms[bestIndex];
                    if (tfm is not null)
                    {
                        var trackerPos = tracker.WorldTranslation;
                        Engine.Rendering.Debug.RenderSphere(trackerPos, CalibrationRadius, false, ColorF4.Green);
                        Engine.Rendering.Debug.RenderLine(trackerPos, tfm.WorldTranslation, ColorF4.Magenta);
                        Engine.Rendering.Debug.RenderPoint(tfm.WorldTranslation, ColorF4.Green);
                    }
                    tfms.RemoveAt(bestIndex);
                }
            }
            foreach (var tfm in tfms)
            {
                if (tfm is null)
                    continue;
                Engine.Rendering.Debug.RenderPoint(tfm.WorldTranslation, ColorF4.Red);
            }
        }

        private bool _isCalibrating = false;
        public bool IsCalibrating
        {
            get => _isCalibrating;
            private set => SetField(ref _isCalibrating, value);
        }

        private float _calibrationRadius = 0.01f;
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

        private Vector3 _eyeOffsetFromHead = Vector3.Zero;
        public Vector3 EyeOffsetFromHead
        {
            get => _eyeOffsetFromHead;
            set => SetField(ref _eyeOffsetFromHead, value);
        }

        public HumanoidComponent? GetHumanoid()
            => HumanoidComponent ?? GetSiblingComponent<HumanoidComponent>();
        public VRTrackerCollectionComponent? GetTrackerCollection()
            => TrackerCollection ?? GetSiblingComponent<VRTrackerCollectionComponent>();
        public CharacterMovement3DComponent? GetCharacterMovement()
            => CharacterMovementComponent ?? GetSiblingComponent<CharacterMovement3DComponent>();

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            ResolveDependencies();
            SetInitialState();
            BeginCalibration();
        }

        private void SetInitialState()
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            EyeOffsetFromHead = h.CalculateEyeOffsetFromHead(EyesModel, EyeLBoneName, EyeRBoneName);
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

        /// <summary>
        /// Moves the playspace and player root to keep the hip centered in the playspace and allow the hip to move the playspace.
        /// </summary>
        /// <param name="hipTrackerTfm"></param>
        private void HipTrackerWorldMatrixChanged(TransformBase hipTrackerTfm)
        {
            var h = GetHumanoid();
            if (h is null || h.HipsTarget.tfm is null)
                return;

            var movement = GetCharacterMovement();
            var playspaceRoot = Transform;
            var playerRoot = h.TransformAs<Transform>(false);
            if (movement is null || playspaceRoot is null || playerRoot is null)
                return;

            var hipMtx = h.HipsTarget.offset * hipTrackerTfm.WorldMatrix;
            var playspaceMtx = playspaceRoot.WorldMatrix;

            Vector3 playspaceToHip = hipMtx.Translation - playspaceMtx.Translation;
            playspaceToHip.Y = 0.0f;
            //Move the playspace the difference between the hip and the playspace, but only in the XZ plane.
            //TODO: use the character controller's up vector to determine the plane
            movement.AddMovementInput(playspaceToHip);

            //Move the player root opposite the hip movement to keep the hip centered in the playspace
            Vector3 rootToHip = hipMtx.Translation - playerRoot.Translation;
            rootToHip.Y = 0.0f;
            playerRoot.Translation = -rootToHip;
        }

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

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            var h = GetHumanoid();
            if (h is null)
                return;

            h.ClearIKTargets();
        }

        private void UpdateRootTransform()
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            var headNode = h.Head.Node;
            if (headNode is null)
                return;

            Matrix4x4 hmdMtx = Engine.VRState.Api.Headset?.DeviceToAbsoluteTrackingMatrix ?? Matrix4x4.Identity;
            Vector3 rootPos = hmdMtx.Translation - h.Head.WorldBindPose.Translation;
            TransformBase.GetDirectionsXZ(hmdMtx, out Vector3 forward, out _);
            Matrix4x4 eyeOffset = Matrix4x4.CreateTranslation(-EyeOffsetFromHead);
            h.SceneNode.Transform.DeriveWorldMatrix(eyeOffset * Matrix4x4.CreateWorld(rootPos, -forward, Globals.Up));
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

            //Tell the humanoid to stop solving IK while we calibrate

            //Root transform should follow the headset
            RegisterTick(ETickGroup.Normal, ETickOrder.Input, UpdateRootTransform);

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

            //Clear all targets
            h.ClearIKTargets();

            //Reset the pose to T-pose
            h.ResetPose();

            IsCalibrating = true;

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
        #endregion

        public void EndCalibration()
        {
            IsCalibrating = false;

            if (Headset is not null)
                UnregisterTick(ETickGroup.Normal, ETickOrder.Input, UpdateRootTransform);

            var h = GetHumanoid();
            if (h is null)
                return;

            h.HeadTarget = (Headset, Matrix4x4.CreateTranslation(-EyeOffsetFromHead));
            h.LeftHandTarget = (LeftController, LeftControllerOffset);
            h.RightHandTarget = (RightController, RightControllerOffset);

            var t = GetTrackerCollection();
            if (t is not null)
            {
                var waistTfm = h.Hips.Node?.Transform;
                var leftFootTfm = h.Left.Foot.Node?.Transform;
                var rightFootTfm = h.Right.Foot.Node?.Transform;

                var chestTfm = h.Chest.Node?.Transform;
                var leftElbowTfm = h.Left.Elbow.Node?.Transform;
                var rightElbowTfm = h.Right.Elbow.Node?.Transform;
                var leftKneeTfm = h.Left.Knee.Node?.Transform;
                var rightKneeTfm = h.Right.Knee.Node?.Transform;

                FindClosestTracker(t, waistTfm, out var WaistTracker, out Matrix4x4 offset);
                h.HipsTarget = (WaistTracker, offset);
                if (WaistTracker is not null)
                    WaistTracker.WorldMatrixChanged += HipTrackerWorldMatrixChanged;

                FindClosestTracker(t, leftFootTfm, out var LeftFootTracker, out offset);
                h.LeftFootTarget = (LeftFootTracker, offset);

                FindClosestTracker(t, rightFootTfm, out var RightFootTracker, out offset);
                h.RightFootTarget = (RightFootTracker, offset);

                FindClosestTracker(t, chestTfm, out var ChestTracker, out offset);
                h.ChestTarget = (ChestTracker, offset);

                FindClosestTracker(t, leftElbowTfm, out var LeftElbowTracker, out offset);
                h.LeftElbowTarget = (LeftElbowTracker, offset);

                FindClosestTracker(t, rightElbowTfm, out var RightElbowTracker, out offset);
                h.RightElbowTarget = (RightElbowTracker, offset);

                FindClosestTracker(t, leftKneeTfm, out var LeftKneeTracker, out offset);
                h.LeftKneeTarget = (LeftKneeTracker, offset);

                FindClosestTracker(t, rightKneeTfm, out var RightKneeTracker, out offset);
                h.RightKneeTarget = (RightKneeTracker, offset);
            }

            h.SolveIK = true;
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

            var bodyPos = humanoidTfm.WorldTranslation;
            float closestDist = float.MaxValue;
            foreach ((VrDevice dev, VRTrackerTransform tracker) in trackerCollection.Trackers.Values)
            {
                var trackerPos = tracker.WorldTranslation;
                var dist = Vector3.DistanceSquared(bodyPos, trackerPos);
                if (dist < closestDist && float.Sqrt(dist) < CalibrationRadius)
                {
                    closestDist = dist;
                    closestTracker = tracker;
                }
            }
            offset = closestTracker is not null ? humanoidTfm.WorldMatrix * closestTracker.InverseWorldMatrix : Matrix4x4.Identity;
        }

        public void CancelCalibration()
        {
            IsCalibrating = false;

            if (Headset is not null)
                UnregisterTick(ETickGroup.Normal, ETickOrder.Input, UpdateRootTransform);

            var h = GetHumanoid();
            if (h is null)
                return;

            h.HeadTarget = LastHeadTarget;
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
            h.SolveIK = true;
        }
    }
}
