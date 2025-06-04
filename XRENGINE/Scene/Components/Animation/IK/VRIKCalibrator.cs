using Extensions;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Calibrates VRIK for the HMD and up to 5 additional trackers.
    /// </summary>
    public static partial class VRIKCalibrator
    {
        private const string LeftHandTargetNodeName = "Left Hand Target";
        private const string RightHandTargetNodeName = "Right Hand Target";

        ///// <summary>
        ///// Recalibrates only the avatar scale, updates CalibrationData to the new scale value
        ///// </summary>
        //public static void RecalibrateScale(VRIKSolverComponent ik, CalibrationData data, Settings settings)
        //    => RecalibrateScale(ik, data, settings.ScaleMultiplier);

        ///// <summary>
        ///// Recalibrates only the avatar scale, updates CalibrationData to the new scale value
        ///// </summary>
        //public static void RecalibrateScale(VRIKSolverComponent ik, CalibrationData data, float scaleMultiplier)
        //{
        //    CalibrateScaleSimple(ik, scaleMultiplier);
        //    data.Scale = ik.Root!.Scale.Y;
        //}

        ///// <summary>
        ///// Calibrates only the avatar scale.
        ///// </summary>
        //private static void CalibrateScale(VRIKSolverComponent ik, Settings settings)
        //    => CalibrateScaleSimple(ik, settings.ScaleMultiplier);

        ///// <summary>
        ///// Calibrates only the avatar scale.
        ///// </summary>
        //private static void CalibrateScaleSimple(VRIKSolverComponent ik, float scaleMultiplier = 1.0f)
        //{
        //    var root = ik.Root;
        //    if (root is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the root transform.");
        //        return;
        //    }
        //    var headTarget = ik.Solver.Spine.HeadTarget;
        //    if (headTarget is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the head target.");
        //        return;
        //    }
        //    var headNode = ik.Humanoid!.Head.Node;
        //    if (headNode is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the head node.");
        //        return;
        //    }

        //    float targetY = headTarget.WorldTranslation.Y - root.WorldTranslation.Y;
        //    float modelY = headNode.Transform.WorldTranslation.Y - root.WorldTranslation.Y;
        //    if (modelY == 0.0f)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK with the model's head node at the same height as the root node.");
        //        return;
        //    }

        //    root.Scale *= targetY / modelY * scaleMultiplier;
        //}

        /// <summary>
        /// Calibrates VRIK to the specified trackers using the VRIKTrackerCalibrator.Settings.
        /// </summary>
        /// <param name="ik">Reference to the VRIK component.</param>
        /// <param name="settings">Calibration settings.</param>
        /// <param name="headTracker">The HMD.</param>
		/// <param name="bodyTracker">(Optional) A tracker placed anywhere on the body of the player, preferrably close to the pelvis, on the belt area.</param>
		/// <param name="leftHandTracker">(Optional) A tracker or hand controller device placed anywhere on or in the player's left hand.</param>
		/// <param name="rightHandTracker">(Optional) A tracker or hand controller device placed anywhere on or in the player's right hand.</param>
		/// <param name="leftFootTracker">(Optional) A tracker placed anywhere on the ankle or toes of the player's left leg.</param>
		/// <param name="rightFootTracker">(Optional) A tracker placed anywhere on the ankle or toes of the player's right leg.</param>
        public static CalibrationData? Calibrate(
            VRIKSolverComponent ik,
            Settings settings,
            TransformBase? headTracker,
            TransformBase? bodyTracker = null,
            TransformBase? leftHandTracker = null,
            TransformBase? rightHandTracker = null,
            TransformBase? leftFootTracker = null,
            TransformBase? rightFootTracker = null)
        {
            if (!ik.Solver.Initialized)
            {
                Debug.LogWarning("Can not calibrate before VRIK has initiated.");
                return null;
            }

            if (headTracker is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the head tracker.");
                return null;
            }

            CalibrationData data = new();

            ik.Solver.ResetTransformToDefault();

            var root = ik.Root;
            if (root is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without a root transform.");
                return null;
            }
            var human = ik.Humanoid;
            if (human is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without a humanoid component.");
                return null;
            }
            var head = human.Head.Node?.GetTransformAs<Transform>(true);
            if (head is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without a head transform.");
                return null;
            }

            var hips = human.Hips.Node?.GetTransformAs<Transform>(true);
            var spine = ik.Solver.Spine;

            //Vector3 headPos = CalibrateRoot(settings, headTracker, root);
            Transform? headTarget = CalibrateHead(headTracker, head, spine);
            CalibrateScale(settings, root, head, headTarget);
            CalibrateHips(ik, settings, bodyTracker, leftFootTracker, rightFootTracker, hips, spine);
            CalibrateLeftHand(ik, settings, leftHandTracker);
            CalibrateRightHand(ik, settings, rightHandTracker);
            CalibrateLeg(
                settings,
                leftFootTracker,
                ik.Solver.LeftLeg,
                (human.Left.Toes.Node ?? human.Left.Foot.Node)?.GetTransformAs<Transform>(true),
                root.WorldForward,
                true);
            CalibrateLeg(
                settings,
                rightFootTracker,
                ik.Solver.RightLeg,
                (human.Right.Toes.Node ?? human.Right.Foot.Node)?.GetTransformAs<Transform>(true),
                root.WorldForward,
                false);

            VRIKRootControllerComponent? rc = CalibrateRootController(bodyTracker, leftFootTracker, rightFootTracker, root);

            // Additional solver settings
            spine.MinHeadHeight = 0.0f;
            //ik.Solver._locomotion._weight = bodyTracker == null && leftFootTracker == null && rightFootTracker == null ? 1.0f : 0.0f;

            // Fill in Calibration Data
            data.Scale = root.Scale.Y;
            data.Head = new CalibrationData.Target(spine.HeadTarget);
            data.Hips = new CalibrationData.Target(spine.HipsTarget);
            data.LeftHand = new CalibrationData.Target(ik.Solver.LeftArm.Target);
            data.RightHand = new CalibrationData.Target(ik.Solver.RightArm.Target);
            data.LeftFoot = new CalibrationData.Target(ik.Solver.LeftLeg.Target);
            data.RightFoot = new CalibrationData.Target(ik.Solver.RightLeg.Target);
            data.LeftLegGoal = new CalibrationData.Target(ik.Solver.LeftLeg.KneeTarget);
            data.RightLegGoal = new CalibrationData.Target(ik.Solver.RightLeg.KneeTarget);
            data.HipsTargetRight = rc != null ? rc.HipsTargetRight : Vector3.Zero;
            data.HipsPositionWeight = spine.HipsPositionWeight;
            data.HipsRotationWeight = spine.HipsRotationWeight;

            return data;
        }

        private static VRIKRootControllerComponent? CalibrateRootController(TransformBase? bodyTracker, TransformBase? leftFootTracker, TransformBase? rightFootTracker, Transform root)
        {
            bool needsRootController = bodyTracker != null || (leftFootTracker != null && rightFootTracker != null);
            var rootController = root.SceneNode?.GetComponent<VRIKRootControllerComponent>();

            if (needsRootController)
            {
                //Create if it doesn't already exist
                rootController ??= root.SceneNode?.AddComponent<VRIKRootControllerComponent>();
                rootController?.Calibrate();
            }
            else
            {
                rootController?.Destroy();
            }

            return rootController;
        }

        //private static Vector3 CalibrateRoot(Settings settings, TransformBase headTracker, Transform root)
        //{
        //    Vector3 headPos =
        //        headTracker.WorldTranslation +
        //        (headTracker.WorldRotation *
        //        XRMath.LookRotation(settings.HeadTrackerForward, settings.HeadTrackerUp)).
        //        Rotate(settings.HeadOffset);

        //    Vector3 headForward = headTracker.WorldRotation.Rotate(settings.HeadTrackerForward).Normalized();
        //    headForward.Y = 0.0f;

        //    Vector3 translation = new(headPos.X, root.WorldTranslation.Y, headPos.Z);
        //    Quaternion rotation = XRMath.LookRotation(headForward, Globals.Up);

        //    //root.SetWorldTranslationRotation(translation, rotation);

        //    root.SaveBindState();
        //    root.RecalculateMatrices(true);
        //    return headPos;
        //}

        ///// <summary>
        ///// Calibrates head IK target to specified anchor position and rotation offset independent of avatar bone orientations.
        ///// </summary>
        //public static void CalibrateHeadSimple(
        //    VRIKSolverComponent ik,
        //    TransformBase centerEyeAnchor,
        //    Vector3 anchorPositionOffset,
        //    Vector3 anchorRotationOffset)
        //{
        //    var root = ik.Root;
        //    if (root is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the root transform.");
        //        return;
        //    }
        //    var head = ik.Humanoid?.Head.Node?.GetTransformAs<Transform>(true);
        //    if (head is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the head transform.");
        //        return;
        //    }

        //    Vector3 forward = Quaternion.Inverse(head.WorldRotation).Rotate(root.WorldForward);
        //    Vector3 up = Quaternion.Inverse(head.WorldRotation).Rotate(root.WorldUp);
        //    Quaternion headSpace = XRMath.LookRotation(forward, up);

        //    Vector3 anchorPos = head.WorldTranslation + (head.WorldRotation * headSpace).Rotate(anchorPositionOffset);
        //    Quaternion anchorRot = head.WorldRotation * headSpace * Quaternion.CreateFromYawPitchRoll(anchorRotationOffset.Y, anchorRotationOffset.X, anchorRotationOffset.Z);
        //    Quaternion anchorRotInverse = Quaternion.Inverse(anchorRot);

        //    var spine = ik.Solver.Spine;
        //    var ht = spine.HeadTarget ??= new SceneNode("Head IK Target").GetTransformAs<Transform>(true)!;
        //    ht.SetParent(centerEyeAnchor, false);
        //    ht.Translation = anchorRotInverse.Rotate(head.WorldTranslation - anchorPos);
        //    ht.Rotation = anchorRotInverse * head.WorldRotation;
        //}

        private static Transform? CalibrateHead(
            TransformBase headTracker,
            Transform head,
            IKSolverVR.SpineSolver spine)
        {
            SceneNode? headTrackerNode = headTracker.SceneNode;
            if (headTrackerNode is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without a root scene node.");
                return spine.HeadTarget;
            }

            Transform headTarget;
            if (spine.HeadTarget is not null)
                headTarget = spine.HeadTarget;
            else
            {
                headTrackerNode.NewChildWithTransform(out headTarget, "Head Target");
                spine.HeadTarget = headTarget;
            }

            head.RecalculateMatrices(true);
            headTarget.SetWorldTranslationRotation(head.WorldTranslation, head.WorldRotation);
            headTarget.SaveBindState();
            headTarget.RecalculateMatrices(true);
            return headTarget;
        }

        private static void CalibrateScale(Settings settings, Transform root, Transform head, Transform? headTarget)
        {
            if (headTarget is null)
            {
                Debug.LogWarning("Can not calibrate scale without a head target.");
                return;
            }
            float targetDist = headTarget.WorldTranslation.Y - root.WorldTranslation.Y;
            float modelDist = head.WorldTranslation.Y - root.WorldTranslation.Y;
            root.Scale *= targetDist / modelDist * settings.ScaleMultiplier;
        }

        private static void CalibrateHips(
            VRIKSolverComponent ik,
            Settings settings,
            TransformBase? hipTracker,
            TransformBase? leftFootTracker,
            TransformBase? rightFootTracker,
            Transform? hips,
            IKSolverVR.SpineSolver spine)
        {
            if (hipTracker is null)
            {
                Debug.LogWarning("Can not calibrate without a hip tracker.");
                return;
            }

            SceneNode? hipTrackerNode = hipTracker.SceneNode;
            if (hipTrackerNode is null)
            {
                Debug.LogWarning("Can not create hips target without a hip tracker scene node.");
                return;
            }

            if (hipTracker != null && hips != null)
            {
                Transform hipsTarget;
                if (spine.HipsTarget is not null)
                    hipsTarget = spine.HipsTarget;
                else
                {
                    hipTrackerNode.NewChildWithTransform(out hipsTarget, "Hips Target");
                    spine.HipsTarget = hipsTarget;
                }

                hips.RecalculateMatrices(true);
                hipsTarget.SetWorldTranslationRotation(hips.WorldTranslation, hips.WorldRotation);
                hipsTarget.RecalculateMatrices(true);
                hipsTarget.SaveBindState();

                spine.HipsPositionWeight = settings.HipPositionWeight;
                spine.HipsRotationWeight = settings.HipRotationWeight;
                spine.MaxRootAngle = 180.0f;

                ik.Solver.PlantFeet = false;
            }
            else if (leftFootTracker != null && rightFootTracker != null)
            {
                ik.Solver.Spine.MaxRootAngle = 0.0f;
            }
        }

        private static void CalibrateLeg(
            Settings settings,
            TransformBase? tracker,
            IKSolverVR.LegSolver leg,
            Transform? lastBone,
            Vector3 rootForward,
            bool isLeft)
        {
            if (tracker is null)
                return;

            var trackerNode = tracker.SceneNode;
            if (trackerNode is null)
            {
                Debug.LogWarning("Can not create bend goal without a tracker scene node.");
                return;
            }
            if (lastBone is null)
            {
                Debug.LogWarning("Can not calibrate leg without the last bone transform.");
                return;
            }

            string name = isLeft ? "Left" : "Right";
            float rightMultiplier = isLeft ? 1.0f : -1.0f;
            CalibrateLeg(settings, tracker, leg, lastBone, rootForward, trackerNode, name, rightMultiplier);
        }

        private static void CalibrateLeg(
            Settings settings,
            TransformBase tracker,
            IKSolverVR.LegSolver leg,
            Transform lastBone,
            Vector3 rootForward,
            SceneNode trackerNode,
            string name,
            float rightMultiplier)
        {
            Transform target;
            if (leg.Target is not null)
                target = leg.Target;
            else
            {
                trackerNode.NewChildWithTransform(out target, $"{name} Leg Target");
                leg.Target = target;
            }

            //Space of the tracker heading
            Quaternion trackerSpace = tracker.WorldRotation * XRMath.LookRotation(settings.FootTrackerForward, settings.FootTrackerUp);
            Vector3 f = trackerSpace.Rotate(Globals.Forward).Normalized();
            f.Y = 0.0f;
            trackerSpace = XRMath.LookRotation(f);

            //Target position
            float inwardOffset = rightMultiplier * settings.FootInwardOffset;

            Quaternion lastBoneWorldRot = lastBone.WorldRotation;

            // Rotate target forward towards tracker forward
            Vector3 footForward = XRMath.GetAxisVectorToDirection(lastBoneWorldRot, rootForward);
            if (Vector3.Dot(lastBoneWorldRot.Rotate(footForward), rootForward) < 0.0f)
                footForward = -footForward;

            Vector3 footOffset = new(inwardOffset, 0.0f, settings.FootForwardOffset);
            Vector3 targetPos = tracker.WorldTranslation + trackerSpace.Rotate(footOffset);
            Vector3 translation = new(targetPos.X, lastBone.WorldTranslation.Y, targetPos.Z);
            Vector3 fLocal = Quaternion.Inverse(XRMath.LookRotation(lastBoneWorldRot.Rotate(footForward))).Rotate(f);
            float yaw = float.RadiansToDegrees(MathF.Atan2(fLocal.X, fLocal.Z));
            float yawOffset = rightMultiplier * settings.FootYawOffset;
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Globals.Up, float.DegreesToRadians(yaw + yawOffset)) * lastBoneWorldRot;
            Vector3 bendGoalTranslation = lastBone.WorldTranslation + trackerSpace.Rotate(Globals.Forward) + trackerSpace.Rotate(Globals.Up);// * 0.5f;

            target.SetWorldTranslationRotation(translation, rotation);
            target.SaveBindState();
            target.RecalculateMatrices(true);

            leg.PositionWeight = 1.0f;
            leg.RotationWeight = 1.0f;

            Transform bendGoal;
            if (leg.KneeTarget is not null)
                bendGoal = leg.KneeTarget;
            else
            {
                trackerNode.NewChildWithTransform(out bendGoal, $"{name} Leg Bend Goal");
                leg.KneeTarget = bendGoal;
            }

            bendGoal.SetWorldTranslation(bendGoalTranslation);
            bendGoal.RecalculateMatrices(true);

            leg.KneeTargetWeight = 1.0f;
        }

        ///// <summary>
        ///// Calibrates VRIK to the specified trackers using CalibrationData from a previous calibration.
        ///// Requires this character's bone orientations to match with the character's that was used in the previous calibration.
        ///// </summary>
        ///// <param name="ik">Reference to the VRIK component.</param>
        ///// <param name="data">Use calibration data from a previous calibration.</param>
        ///// <param name="headTracker">The HMD.</param>
        ///// <param name="bodyTracker">(Optional) A tracker placed anywhere on the body of the player, preferrably close to the pelvis, on the belt area.</param>
        ///// <param name="leftHandTracker">(Optional) A tracker or hand controller device placed anywhere on or in the player's left hand.</param>
        ///// <param name="rightHandTracker">(Optional) A tracker or hand controller device placed anywhere on or in the player's right hand.</param>
        ///// <param name="leftFootTracker">(Optional) A tracker placed anywhere on the ankle or toes of the player's left leg.</param>
        ///// <param name="rightFootTracker">(Optional) A tracker placed anywhere on the ankle or toes of the player's right leg.</param>
        //public static void Calibrate(
        //    VRIKSolverComponent ik,
        //    CalibrationData data,
        //    TransformBase headTracker,
        //    TransformBase? bodyTracker = null,
        //    TransformBase? leftHandTracker = null,
        //    TransformBase? rightHandTracker = null,
        //    TransformBase? leftFootTracker = null,
        //    TransformBase? rightFootTracker = null)
        //{
        //    if (!ik.Solver.Initialized)
        //    {
        //        Debug.LogWarning("Can not calibrate before VRIK has initiated.");
        //        return;
        //    }

        //    if (headTracker == null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the head tracker.");
        //        return;
        //    }

        //    var root = ik.Root;
        //    if (root is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without a root transform.");
        //        return;
        //    }

        //    var spine = ik.Solver.Spine;

        //    ik.Solver.ResetTransformToDefault();

        //    CalibrateHead(data, headTracker, spine);
        //    CalibrateScale(data, root);
        //    CalibrateHips(ik, data, bodyTracker, leftFootTracker, rightFootTracker, spine);
        //    CalibrateLeftHand(ik, data, leftHandTracker);
        //    CalibrateRightHand(ik, data, rightHandTracker);
        //    CalibrateLeg(
        //        data,
        //        leftFootTracker,
        //        ik.Solver.LeftLeg,
        //        (ik.Humanoid?.Left.Toes.Node ?? ik.Humanoid?.Left.Foot.Node)?.GetTransformAs<Transform>(true),
        //        root.WorldForward,
        //        true);
        //    CalibrateLeg(
        //        data,
        //        rightFootTracker,
        //        ik.Solver.RightLeg,
        //        (ik.Humanoid?.Right.Toes.Node ?? ik.Humanoid?.Right.Foot.Node)?.GetTransformAs<Transform>(true),
        //        root.WorldForward,
        //        false);

        //    // Additional solver settings
        //    spine.MinHeadHeight = 0.0f;
        //    //ik.Solver._locomotion._weight = bodyTracker == null && leftFootTracker == null && rightFootTracker == null ? 1.0f : 0.0f;

        //    //CalibrateRoot(data, bodyTracker, leftFootTracker, rightFootTracker, root);
        //}

        //private static void CalibrateRightHand(VRIKSolverComponent ik, CalibrationData data, TransformBase? rightHandTracker)
        //{
        //    if (rightHandTracker is null)
        //    {
        //        Debug.LogWarning("Can not calibrate right hand without a right hand tracker.");
        //        return;
        //    }

        //    SceneNode? rightHandNode = rightHandTracker.SceneNode;
        //    if (rightHandNode is null)
        //    {
        //        Debug.LogWarning("Can not create right hand target without a right hand tracker scene node.");
        //        return;
        //    }

        //    var rightArm = ik.Solver.RightArm;
        //    float weightVal = 0.0f;
        //    if (rightHandTracker != null)
        //    {
        //        weightVal = 1.0f;
        //        Transform rightHandTarget = GetOrAddHandTarget(rightHandNode, rightArm, RightHandTargetNodeName);
        //        data.RightHand?.ApplyTo(rightHandTarget);
        //    }
        //    rightArm.PositionWeight = weightVal;
        //    rightArm.RotationWeight = weightVal;
        //}

        private static void CalibrateLeftHand(VRIKSolverComponent ik, Settings settings, TransformBase? leftControllerTfm)
            => CalibrateHand(settings, leftControllerTfm, 1.0f, LeftHandTargetNodeName, ik.Solver.LeftArm);
        private static void CalibrateRightHand(VRIKSolverComponent ik, Settings settings, TransformBase? rightControllerTfm)
            => CalibrateHand(settings, rightControllerTfm, -1.0f, RightHandTargetNodeName, ik.Solver.RightArm);
        private static void CalibrateHand(Settings settings, TransformBase? controllerTfm, float palmCrossNegate, string targetNodeName, IKSolverVR.ArmSolver arm)
        {
            if (controllerTfm is null)
            {
                Debug.LogWarning("Can not calibrate hand without a controller transform.");
                return;
            }

            SceneNode? controllerNode = controllerTfm.SceneNode;
            if (controllerNode is null)
            {
                Debug.LogWarning("Can not create hand target without a controller scene node.");
                return;
            }

            float weightVal = 0.0f;
            if (controllerTfm != null)
            {
                weightVal = 1.0f;
                Quaternion look = controllerTfm.WorldRotation * XRMath.LookRotation(settings.HandTrackerForward, settings.HandTrackerUp);
                Vector3 translation = controllerTfm.WorldTranslation + look.Rotate(settings.HandOffset);
                Quaternion rotation = XRMath.MatchRotation(
                    look,
                    settings.HandTrackerForward,
                    settings.HandTrackerUp,
                    arm.WristToPalmAxis,
                    palmCrossNegate * Vector3.Cross(arm.WristToPalmAxis, arm.PalmToThumbAxis));

                Transform handTarget = GetOrAddHandTarget(controllerNode, arm, targetNodeName);
                controllerTfm.RecalculateMatrices(true);
                handTarget.SetWorldTranslationRotation(translation, rotation);
                handTarget.SaveBindState();
                handTarget.RecalculateMatrices(true);
            }
            arm.PositionWeight = weightVal;
            arm.RotationWeight = weightVal;
        }

        //private static void CalibrateLeftHand(VRIKSolverComponent ik, CalibrationData data, TransformBase? leftHandTracker)
        //{
        //    if (leftHandTracker is null)
        //    {
        //        Debug.LogWarning("Can not calibrate left hand without a left hand tracker.");
        //        return;
        //    }

        //    SceneNode? leftHandNode = leftHandTracker.SceneNode;
        //    if (leftHandNode is null)
        //    {
        //        Debug.LogWarning("Can not create left hand target without a left hand tracker scene node.");
        //        return;
        //    }

        //    var leftArm = ik.Solver.LeftArm;
        //    float weightVal = 0.0f;
        //    if (leftHandTracker != null)
        //    {
        //        weightVal = 1.0f;
        //        Transform leftHandTarget = GetOrAddHandTarget(leftHandNode, leftArm, LeftHandTargetNodeName);
        //        data.LeftHand?.ApplyTo(leftHandTarget);
        //    }
        //    leftArm.PositionWeight = weightVal;
        //    leftArm.RotationWeight = weightVal;
        //}

        private static Transform GetOrAddHandTarget(SceneNode handNode, IKSolverVR.ArmSolver arm, string name)
        {
            Transform target;
            if (arm.Target is null)
            {
                handNode.NewChildWithTransform(out target, name);
                arm.Target = target;
            }
            else
                target = arm.Target;
            return target;
        }

        //private static void CalibrateHips(
        //    VRIKSolverComponent ik,
        //    CalibrationData data,
        //    TransformBase? hipTracker,
        //    TransformBase? leftFootTracker,
        //    TransformBase? rightFootTracker,
        //    IKSolverVR.SpineSolver spine)
        //{
        //    if (hipTracker != null && data.Hips != null)
        //    {
        //        SceneNode? hipTrackerNode = hipTracker.SceneNode;
        //        if (hipTrackerNode is null)
        //        {
        //            Debug.LogWarning("Can not create hips target without a body tracker scene node.");
        //            return;
        //        }

        //        Transform hipsTarget;
        //        if (spine.HipsTarget != null)
        //            hipsTarget = spine.HipsTarget;
        //        else
        //            hipTrackerNode.NewChildWithTransform(out hipsTarget, "Hips Target");
                
        //        data.Hips.ApplyTo(hipsTarget);
        //        spine.HipsTarget = hipsTarget;

        //        spine.HipsPositionWeight = data.HipsPositionWeight;
        //        spine.HipsRotationWeight = data.HipsRotationWeight;
        //        spine.MaxRootAngle = 180.0f;

        //        ik.Solver.PlantFeet = false;
        //    }
        //    else if (leftFootTracker != null && rightFootTracker != null)
        //    {
        //        spine.MaxRootAngle = 0.0f;
        //    }
        //}

        //private static void CalibrateScale(CalibrationData data, Transform root)
        //{
        //    root.Scale = data.Scale * Vector3.One;
        //}

        //private static void CalibrateHead(CalibrationData data, TransformBase headTracker, IKSolverVR.SpineSolver spine)
        //{
        //    if (headTracker is null)
        //    {
        //        Debug.LogWarning("Can not calibrate without a head tracker.");
        //        return;
        //    }

        //    SceneNode? headTrackerNode = headTracker.SceneNode;
        //    if (headTrackerNode is null)
        //    {
        //        Debug.LogWarning("Can not create head target without a head tracker scene node.");
        //        return;
        //    }

        //    Transform headTarget;
        //    if (spine.HeadTarget is not null)
        //        headTarget = spine.HeadTarget;
        //    else
        //        headTrackerNode.NewChildWithTransform(out headTarget, "Head Target");
            
        //    data.Head?.ApplyTo(headTarget);
        //    spine.HeadTarget = headTarget;
        //}

        //private static void CalibrateRoot(CalibrationData data, TransformBase? bodyTracker, TransformBase? leftFootTracker, TransformBase? rightFootTracker, Transform root)
        //{
        //    var node = root.SceneNode;
        //    if (node is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without a root scene node.");
        //        return;
        //    }

        //    var rc = node.GetComponent<VRIKRootControllerComponent>();
        //    if (rc is null)
        //        return;

        //    bool addRootController = bodyTracker != null || (leftFootTracker != null && rightFootTracker != null);
        //    if (addRootController)
        //    {
        //        rc ??= node.AddComponent<VRIKRootControllerComponent>()!;
        //        rc?.Calibrate(data);
        //    }
        //    else
        //        rc?.Destroy();
        //}

        //private static void CalibrateLeg(
        //    CalibrationData data,
        //    TransformBase? tracker,
        //    IKSolverVR.LegSolver leg,
        //    TransformBase? lastBone,
        //    Vector3 rootForward,
        //    bool isLeft)
        //{
        //    if (tracker is null)
        //        return;

        //    SceneNode? trackerNode = tracker.SceneNode;
        //    if (trackerNode is null)
        //    {
        //        Debug.LogWarning($"Can not calibrate leg without a tracker scene node.");
        //        return;
        //    }

        //    if ((isLeft && data.LeftFoot is null) || 
        //        (!isLeft && data.RightFoot is null))
        //        return;

        //    string sideName;
        //    CalibrationData.Target? foot;
        //    CalibrationData.Target? targetLegGoal;
        //    if (isLeft)
        //    {
        //        sideName = "Left";
        //        foot = data.LeftFoot;
        //        targetLegGoal = data.LeftLegGoal;
        //    }
        //    else
        //    {
        //        sideName = "Right";
        //        foot = data.RightFoot;
        //        targetLegGoal = data.RightLegGoal;
        //    }

        //    Transform target;
        //    if (leg.Target != null)
        //        target = leg.Target;
        //    else
        //        trackerNode.NewChildWithTransform(out target, $"{sideName} Foot Target");
            
        //    foot?.ApplyTo(target);
        //    leg.Target = target;
        //    leg.PositionWeight = 1.0f;
        //    leg.RotationWeight = 1.0f;

        //    Transform bendGoal;
        //    if (leg.BendGoal != null)
        //        bendGoal = leg.BendGoal;
        //    else
        //        trackerNode.NewChildWithTransform(out bendGoal, $"{sideName} Leg Bend Goal");
            
        //    targetLegGoal?.ApplyTo(bendGoal);
        //    leg.BendGoal = bendGoal;
        //    leg.BendGoalWeight = 1.0f;
        //}

        ///// <summary>
        ///// Simple calibration to head and hands using predefined anchor position and rotation offsets.
        ///// </summary>
        ///// <param name="ik">The VRIK component.</param>
        ///// <param name="centerEyeAnchor">HMD.</param>
        ///// <param name="leftHandAnchor">Left hand controller.</param>
        ///// <param name="rightHandAnchor">Right hand controller.</param>
        ///// <param name="centerEyePositionOffset">Position offset of the camera from the head bone (root space).</param>
        ///// <param name="centerEyeRotationOffset">Rotation offset of the camera from the head bone (root space).</param>
        ///// <param name="handPositionOffset">Position offset of the hand controller from the hand bone (controller space).</param>
        ///// <param name="handRotationOffset">Rotation offset of the hand controller from the hand bone (controller space).</param>
        ///// <param name="scale">Multiplies the scale of the root.</param>
        ///// <returns></returns>
        //public static CalibrationData Calibrate(
        //    VRIKSolverComponent ik,
        //    TransformBase centerEyeAnchor,
        //    TransformBase leftHandAnchor,
        //    TransformBase rightHandAnchor,
        //    Vector3 centerEyePositionOffset,
        //    Vector3 centerEyeRotationOffset,
        //    Vector3 handPositionOffset,
        //    Vector3 handRotationOffset,
        //    float scale = 1.0f)
        //{
        //    CalibrateHeadSimple(ik, centerEyeAnchor, centerEyePositionOffset, centerEyeRotationOffset);
        //    CalibrateHandsSimple(ik, leftHandAnchor, rightHandAnchor, handPositionOffset, handRotationOffset);
        //    CalibrateScaleSimple(ik, scale);
        //    return new()
        //    {
        //        Scale = ik.Root?.Scale.Y ?? 1.0f,
        //        Head = new CalibrationData.Target(ik.Solver.Spine.HeadTarget),
        //        LeftHand = new CalibrationData.Target(ik.Solver.LeftArm.Target),
        //        RightHand = new CalibrationData.Target(ik.Solver.RightArm.Target)
        //    };
        //}

        ///// <summary>
        ///// Calibrates body target to avatar pelvis position and position/rotation offsets in character root space.
        ///// </summary>
        //public static void CalibrateBody(
        //    VRIKSolverComponent ik,
        //    Transform hipsTracker,
        //    Vector3 trackerPositionOffset,
        //    Vector3 trackerRotationOffset)
        //{
        //    var spine = ik.Solver.Spine;
        //    var root = ik.Root;
        //    if (root is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the root transform.");
        //        return;
        //    }
        //    var hips = ik.Humanoid?.Hips.Node?.GetTransformAs<Transform>(true);
        //    if (hips is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the hips transform.");
        //        return;
        //    }

        //    var ht = spine.HipsTarget ??= new SceneNode(ik.World, "Hips IK Target").GetTransformAs<Transform>(true)!;
        //    ht.SetParent(hipsTracker, false);
        //    ht.SetWorldTranslation(hips.WorldTranslation + root.WorldRotation.Rotate(trackerPositionOffset));
        //    ht.SetWorldRotation(root.WorldRotation * Quaternion.CreateFromYawPitchRoll(trackerRotationOffset.Y, trackerPositionOffset.X, trackerPositionOffset.Z));
        //}

        ///// <summary>
        ///// Calibrates hand IK targets to specified anchor position and rotation offsets independent of avatar bone orientations.
        ///// </summary>
        //public static void CalibrateHandsSimple(
        //    VRIKSolverComponent ik,
        //    TransformBase leftHandAnchor,
        //    TransformBase rightHandAnchor,
        //    Vector3 anchorPositionOffset,
        //    Vector3 anchorRotationOffset)
        //{
        //    ik.Solver.LeftArm.Target ??= new SceneNode(ik.World, "Left Hand IK Target").GetTransformAs<Transform>(true)!;
        //    ik.Solver.RightArm.Target ??= new SceneNode(ik.World, "Right Hand IK Target").GetTransformAs<Transform>(true)!;

        //    CalibrateHand(ik, leftHandAnchor, anchorPositionOffset, anchorRotationOffset, true);
        //    CalibrateHand(ik, rightHandAnchor, anchorPositionOffset, anchorRotationOffset, false);
        //}

        //private static void CalibrateHand(
        //    VRIKSolverComponent ik,
        //    TransformBase anchor,
        //    Vector3 positionOffset,
        //    Vector3 rotationOffset,
        //    bool isLeft)
        //{
        //    if (isLeft)
        //    {
        //        positionOffset.X = -positionOffset.X;
        //        rotationOffset.Y = -rotationOffset.Y;
        //        rotationOffset.Z = -rotationOffset.Z;
        //    }

        //    var human = ik.Humanoid;
        //    if (human is null)
        //    {
        //        Debug.LogWarning("Can not calibrate VRIK without the humanoid component.");
        //        return;
        //    }

        //    var hand = (isLeft ? human.Left.Wrist.Node : human.Right.Wrist.Node)?.GetTransformAs<Transform>(true);
        //    if (hand is null)
        //    {
        //        Debug.LogWarning($"Can not calibrate VRIK without the {human.Name} hand transform.");
        //        return;
        //    }
        //    var target = isLeft ? ik.Solver.LeftArm.Target : ik.Solver.RightArm.Target;
        //    if (target is null)
        //    {
        //        Debug.LogWarning($"Can not calibrate VRIK without the {human.Name} hand target.");
        //        return;
        //    }

        //    var forearm = (isLeft ? human.Left.Elbow.Node : human.Right.Elbow.Node)?.GetTransformAs<Transform>(true);

        //    Vector3 forward = isLeft
        //        ? ik.Solver.LeftArm.WristToPalmAxis
        //        : ik.Solver.RightArm.WristToPalmAxis;

        //    if (forward == Vector3.Zero)
        //        forward = GuessWristToPalmAxis(hand, forearm);

        //    Vector3 up = isLeft
        //        ? ik.Solver.LeftArm.PalmToThumbAxis
        //        : ik.Solver.RightArm.PalmToThumbAxis;

        //    if (up == Vector3.Zero)
        //        up = GuessPalmToThumbAxis(hand, forearm);

        //    Quaternion handSpace = XRMath.LookRotation(forward, up);
        //    Vector3 anchorPos = hand.WorldTranslation + (hand.WorldRotation * handSpace).Rotate(positionOffset);
        //    Quaternion anchorRot = hand.WorldRotation * handSpace * Quaternion.CreateFromYawPitchRoll(rotationOffset.Y, rotationOffset.X, rotationOffset.Z);
        //    Quaternion anchorRotInverse = Quaternion.Inverse(anchorRot);

        //    target.SetParent(anchor, false);
        //    target.Translation = anchorRotInverse.Rotate(hand.WorldTranslation - anchorPos);
        //    target.Rotation = anchorRotInverse * hand.WorldRotation;
        //}

        public static Vector3 GuessWristToPalmAxis(Transform? hand, Transform? forearm)
        {
            if (hand is null || forearm is null)
            {
                Debug.LogWarning("Can not guess the hand bone's orientation without the hand and forearm transforms.");
                return Vector3.Zero;
            }

            Vector3 handToForearm = forearm.WorldTranslation - hand.WorldTranslation;
            var majorDir = XRMath.GetAxisToDirection(hand.WorldRotation, handToForearm);
            Vector3 axis = XRMath.AxisToVector(majorDir);
            if (Vector3.Dot(handToForearm, hand.WorldRotation.Rotate(axis)) > 0.0f)
                axis = -axis;

            return axis;
        }

        public static Vector3 GuessPalmToThumbAxis(Transform? hand, Transform? forearm)
        {
            if (hand is null || forearm is null) 
                return Vector3.Zero;

            if (hand.ChildCount == 0)
            {
                Debug.LogWarning($"Hand {hand.Name} does not have any fingers, VRIK can not guess the hand bone's orientation." +
                    $" Please assign 'Wrist To Palm Axis' and 'Palm To Thumb Axis' manually for both arms in VRIK settings.");
                return Vector3.Zero;
            }

            float closestSqrMag = float.PositiveInfinity;
            int thumbIndex = 0;

            for (int i = 0; i < hand.ChildCount; i++)
            {
                TransformBase? finger = hand.GetChild(i);
                if (finger is null)
                    continue;

                float sqrMag = (finger.WorldTranslation - hand.WorldTranslation).LengthSquared();
                if (sqrMag < closestSqrMag)
                {
                    closestSqrMag = sqrMag;
                    thumbIndex = i;
                }
            }

            TransformBase? thumb = hand.GetChild(thumbIndex);
            if (thumb is null)
            {
                Debug.LogWarning($"Hand {hand.Name} does not have a thumb, VRIK can not guess the hand bone's orientation." +
                    $" Please assign 'Wrist To Palm Axis' and 'Palm To Thumb Axis' manually for both arms in VRIK settings.");
                return Vector3.Zero;
            }

            Vector3 handNormal = Vector3.Cross(hand.WorldTranslation - forearm.WorldTranslation, thumb.WorldTranslation - hand.WorldTranslation);
            Vector3 toThumb = Vector3.Cross(handNormal, hand.WorldTranslation - forearm.WorldTranslation);
            Vector3 axis = XRMath.AxisToVector(XRMath.GetAxisToDirection(hand.WorldRotation, toThumb));
            if (Vector3.Dot(toThumb, hand.WorldRotation.Rotate(axis)) < 0.0f)
                axis = -axis;

            return axis;
        }
    }
}
