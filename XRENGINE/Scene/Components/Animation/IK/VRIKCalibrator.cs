using Extensions;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Calibrates VRIK for the HMD and up to 5 additional trackers.
    /// </summary>
    public static partial class VRIKCalibrator
    {
        /// <summary>
        /// Recalibrates only the avatar scale, updates CalibrationData to the new scale value
        /// </summary>
        public static void RecalibrateScale(VRIKSolverComponent ik, CalibrationData data, Settings settings)
            => RecalibrateScale(ik, data, settings.ScaleMultiplier);

        /// <summary>
        /// Recalibrates only the avatar scale, updates CalibrationData to the new scale value
        /// </summary>
        public static void RecalibrateScale(VRIKSolverComponent ik, CalibrationData data, float scaleMultiplier)
        {
            CalibrateScale(ik, scaleMultiplier);
            data._scale = ik.Root!.Scale.Y;
        }

        /// <summary>
        /// Calibrates only the avatar scale.
        /// </summary>
        private static void CalibrateScale(VRIKSolverComponent ik, Settings settings)
        {
            CalibrateScale(ik, settings.ScaleMultiplier);
        }

        /// <summary>
        /// Calibrates only the avatar scale.
        /// </summary>
        private static void CalibrateScale(VRIKSolverComponent ik, float scaleMultiplier = 1f)
        {
            var root = ik.Root;
            if (root is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the root transform.");
                return;
            }
            var headTarget = ik.Solver._spine._headTarget;
            if (headTarget is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the head target.");
                return;
            }
            var headNode = ik.Humanoid!.Head.Node;
            if (headNode is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the head node.");
                return;
            }

            float targetY = headTarget.WorldTranslation.Y - root.WorldTranslation.Y;
            float modelY = headNode.Transform.WorldTranslation.Y - root.WorldTranslation.Y;
            if (modelY == 0f)
            {
                Debug.LogWarning("Can not calibrate VRIK with the model's head node at the same height as the root node.");
                return;
            }

            root.Scale *= targetY / modelY * scaleMultiplier;
        }

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
            Transform headTracker,
            Transform? bodyTracker = null,
            Transform? leftHandTracker = null,
            Transform? rightHandTracker = null,
            Transform? leftFootTracker = null,
            Transform? rightFootTracker = null)
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
            var leftArm = human.Left.Arm.Node?.GetTransformAs<Transform>(true);
            var spine = ik.Solver._spine;

            // Root position and rotation
            Vector3 headPos = 
                headTracker.WorldTranslation + 
                (headTracker.WorldRotation * 
                XRMath.LookRotation(settings.HeadTrackerForward, settings.HeadTrackerUp)).
                Rotate(settings.HeadOffset);

            root.SetWorldTranslation(new Vector3(headPos.X, root.WorldTranslation.Y, headPos.Z));
            Vector3 headForward = headTracker.WorldRotation.Rotate(settings.HeadTrackerForward);
            headForward.Y = 0f;
            root.SetWorldRotation(XRMath.LookRotation(headForward, Globals.Up));

            // Head
            Transform headTarget = spine._headTarget ?? (new SceneNode(root.World, "Head Target")).GetTransformAs<Transform>(true)!;

            headTarget.SetParent(headTracker, false);
            headTarget.SetWorldTranslation(headPos);
            headTarget.SetWorldRotation(head.WorldRotation);

            spine._headTarget = headTarget;

            // Size
            float targetDist = headTarget.WorldTranslation.Y - root.WorldTranslation.Y;
            float modelDist = head.WorldTranslation.Y - root.WorldTranslation.Y;
            root.Scale *= targetDist / modelDist * settings.ScaleMultiplier;

            // Body
            if (bodyTracker != null && hips != null)
            {
                Transform pelvisTarget = spine._hipsTarget ?? (new SceneNode(root.World, "Hips Target")).GetTransformAs<Transform>(true)!;

                pelvisTarget.SetParent(bodyTracker, false);
                pelvisTarget.SetWorldTranslation(hips.WorldTranslation);
                pelvisTarget.SetWorldRotation(hips.WorldRotation);

                spine._hipsTarget = pelvisTarget;
                spine._pelvisPositionWeight = settings.HipPositionWeight;
                spine._pelvisRotationWeight = settings.HipRotationWeight;
                spine._maxRootAngle = 180f;

                ik.Solver._plantFeet = false;
            }
            else if (leftFootTracker != null && rightFootTracker != null)
            {
                ik.Solver._spine._maxRootAngle = 0f;
            }

            // Left Hand
            if (leftHandTracker != null)
            {
                Transform leftHandTarget = ik.Solver._leftArm._target ?? (new SceneNode("Left Hand Target")).GetTransformAs<Transform>(true)!;

                Quaternion look = leftHandTracker.WorldRotation * XRMath.LookRotation(settings.HandTrackerForward, settings.HandTrackerUp);

                leftHandTarget.SetParent(leftHandTracker, false);
                leftHandTarget.SetWorldTranslation(leftHandTracker.WorldTranslation + look.Rotate(settings.HandOffset));
                leftHandTarget.SetWorldRotation(
                    XRMath.MatchRotation(look,
                    settings.HandTrackerForward,
                    settings.HandTrackerUp, 
                    ik.Solver._leftArm._wristToPalmAxis,
                    Vector3.Cross(ik.Solver._leftArm._wristToPalmAxis, ik.Solver._leftArm._palmToThumbAxis)));

                ik.Solver._leftArm._target = leftHandTarget;
                ik.Solver._leftArm._positionWeight = 1f;
                ik.Solver._leftArm._rotationWeight = 1f;
            }
            else
            {
                ik.Solver._leftArm._positionWeight = 0f;
                ik.Solver._leftArm._rotationWeight = 0f;
            }

            // Right Hand
            if (rightHandTracker != null)
            {
                Transform rightHandTarget = ik.Solver._rightArm._target ?? (new SceneNode("Right Hand Target")).GetTransformAs<Transform>(true)!;

                Quaternion look = rightHandTracker.WorldRotation * XRMath.LookRotation(settings.HandTrackerForward, settings.HandTrackerUp);

                rightHandTarget.SetParent(rightHandTracker, false);
                rightHandTarget.SetWorldTranslation(rightHandTracker.WorldTranslation + look.Rotate(settings.HandOffset));
                rightHandTarget.SetWorldRotation(XRMath.MatchRotation(
                    look,
                    settings.HandTrackerForward,
                    settings.HandTrackerUp,
                    ik.Solver._rightArm._wristToPalmAxis,
                    -Vector3.Cross(ik.Solver._rightArm._wristToPalmAxis, ik.Solver._rightArm._palmToThumbAxis)));

                ik.Solver._rightArm._target = rightHandTarget;
                ik.Solver._rightArm._positionWeight = 1f;
                ik.Solver._rightArm._rotationWeight = 1f;
            }
            else
            {
                ik.Solver._rightArm._positionWeight = 0f;
                ik.Solver._rightArm._rotationWeight = 0f;
            }

            // Legs
            if (leftFootTracker != null)
            {
                CalibrateLeg(
                    settings,
                    leftFootTracker,
                    ik.Solver._leftLeg,
                    (human.Left.Toes.Node ?? human.Left.Foot.Node)?.GetTransformAs<Transform>(true),
                    root.WorldForward,
                    true);
            }
            if (rightFootTracker != null)
            {
                CalibrateLeg(
                    settings,
                    rightFootTracker,
                    ik.Solver._rightLeg,
                    (human.Right.Toes.Node ?? human.Right.Foot.Node)?.GetTransformAs<Transform>(true),
                    root.WorldForward,
                    false);
            }

            // Root controller
            bool addRootController = bodyTracker != null || (leftFootTracker != null && rightFootTracker != null);
            var rootController = root.SceneNode?.GetComponent<VRIKRootControllerComponent>();

            if (addRootController)
            {
                rootController ??= root.SceneNode?.AddComponent<VRIKRootControllerComponent>();
                rootController?.Calibrate();
            }
            else
            {
                rootController?.Destroy();
            }

            // Additional solver settings
            spine._minHeadHeight = 0f;
            ik.Solver._locomotion._weight = bodyTracker == null && leftFootTracker == null && rightFootTracker == null ? 1f : 0f;

            // Fill in Calibration Data
            data._scale = root.Scale.Y;
            data._head = new CalibrationData.Target(spine._headTarget);
            data._pelvis = new CalibrationData.Target(spine._hipsTarget);
            data._leftHand = new CalibrationData.Target(ik.Solver._leftArm._target);
            data._rightHand = new CalibrationData.Target(ik.Solver._rightArm._target);
            data._leftFoot = new CalibrationData.Target(ik.Solver._leftLeg._target);
            data._rightFoot = new CalibrationData.Target(ik.Solver._rightLeg._target);
            data._leftLegGoal = new CalibrationData.Target(ik.Solver._leftLeg._bendGoal);
            data._rightLegGoal = new CalibrationData.Target(ik.Solver._rightLeg._bendGoal);
            data._pelvisTargetRight = rootController != null ? rootController.PelvisTargetRight : Vector3.Zero;
            data._pelvisPositionWeight = ik.Solver._spine._pelvisPositionWeight;
            data._pelvisRotationWeight = ik.Solver._spine._pelvisRotationWeight;

            return data;
        }

        private static void CalibrateLeg(Settings settings, Transform tracker, IKSolverVR.Leg leg, Transform? lastBone, Vector3 rootForward, bool isLeft)
        {
            if (lastBone is null)
            {
                Debug.LogWarning("Can not calibrate leg without the last bone transform.");
                return;
            }

            string name = isLeft ? "Left" : "Right";
            Transform target = leg._target ?? new SceneNode(tracker.World, $"{name} Foot Target").GetTransformAs<Transform>(true)!;

            // Space of the tracker heading
            Quaternion trackerSpace = tracker.WorldRotation * XRMath.LookRotation(settings.FootTrackerForward, settings.FootTrackerUp);
            Vector3 f = trackerSpace.Rotate(Globals.Forward);
            f.Y = 0f;
            trackerSpace = XRMath.LookRotation(f);

            // Target position
            float inwardOffset = settings.FootInwardOffset;
            if (!isLeft)
                inwardOffset = -inwardOffset;

            var targetPos = tracker.WorldTranslation + trackerSpace.Rotate(new Vector3(inwardOffset, 0f, settings.FootForwardOffset));
            target.SetWorldTranslation(new Vector3(targetPos.X, lastBone.WorldTranslation.Y, targetPos.Z));

            // Target rotation
            target.SetWorldRotation(lastBone.WorldRotation);

            // Rotate target forward towards tracker forward
            Vector3 footForward = XRMath.GetAxisVectorToDirection(lastBone.WorldRotation, rootForward);
            if (Vector3.Dot(lastBone.WorldRotation.Rotate(footForward), rootForward) < 0f) 
                footForward = -footForward;

            Vector3 fLocal = Quaternion.Inverse(XRMath.LookRotation(target.WorldRotation.Rotate(footForward))).Rotate(f);
            float yaw = float.RadiansToDegrees(MathF.Atan2(fLocal.X, fLocal.Z));

            float yawOffset = settings.FootYawOffset;
            if (!isLeft)
                yawOffset = -yawOffset;

            target.SetWorldRotation(Quaternion.CreateFromAxisAngle(Globals.Up, float.DegreesToRadians(yaw + yawOffset)) * target.WorldRotation);

            target.SetParent(tracker, false);
            leg._target = target;

            leg._positionWeight = 1f;
            leg._rotationWeight = 1f;

            // Bend goal
            Transform bendGoal = leg._bendGoal ?? (new SceneNode(tracker.World, $"{name} Leg Bend Goal")).GetTransformAs<Transform>(true)!;
            bendGoal.SetWorldTranslation(lastBone.WorldTranslation + trackerSpace.Rotate(Globals.Forward) + trackerSpace.Rotate(Globals.Up));// * 0.5f;
            bendGoal.SetParent(tracker, true);
            leg._bendGoal = bendGoal;
            leg._bendGoalWeight = 1f;
        }

        /// <summary>
        /// Calibrates VRIK to the specified trackers using CalibrationData from a previous calibration.
        /// Requires this character's bone orientations to match with the character's that was used in the previous calibration.
        /// </summary>
        /// <param name="ik">Reference to the VRIK component.</param>
        /// <param name="data">Use calibration data from a previous calibration.</param>
        /// <param name="headTracker">The HMD.</param>
        /// <param name="bodyTracker">(Optional) A tracker placed anywhere on the body of the player, preferrably close to the pelvis, on the belt area.</param>
        /// <param name="leftHandTracker">(Optional) A tracker or hand controller device placed anywhere on or in the player's left hand.</param>
        /// <param name="rightHandTracker">(Optional) A tracker or hand controller device placed anywhere on or in the player's right hand.</param>
        /// <param name="leftFootTracker">(Optional) A tracker placed anywhere on the ankle or toes of the player's left leg.</param>
        /// <param name="rightFootTracker">(Optional) A tracker placed anywhere on the ankle or toes of the player's right leg.</param>
        public static void Calibrate(
            VRIKSolverComponent ik,
            CalibrationData data,
            Transform headTracker,
            Transform? bodyTracker = null,
            Transform? leftHandTracker = null,
            Transform? rightHandTracker = null,
            Transform? leftFootTracker = null,
            Transform? rightFootTracker = null)
        {
            if (!ik.Solver.Initialized)
            {
                Debug.LogWarning("Can not calibrate before VRIK has initiated.");
                return;
            }

            if (headTracker == null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the head tracker.");
                return;
            }

            var root = ik.Root;
            if (root is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without a root transform.");
                return;
            }

            var spine = ik.Solver._spine;

            ik.Solver.ResetTransformToDefault();

            // Head
            Transform headTarget = spine._headTarget ?? (new SceneNode(root.World, "Head Target")).GetTransformAs<Transform>(true)!;

            headTarget.SetParent(headTracker, false);
            data._head?.ApplyLocalTransformTo(headTarget);
            spine._headTarget = headTarget;

            // Size
            root.Scale = data._scale * Vector3.One;

            // Body
            if (bodyTracker != null && data._pelvis != null)
            {
                Transform pelvisTarget = spine._hipsTarget ?? (new SceneNode(root.World, "Pelvis Target")).GetTransformAs<Transform>(true)!;

                pelvisTarget.SetParent(bodyTracker, false);
                data._pelvis.ApplyLocalTransformTo(pelvisTarget);
                spine._hipsTarget = pelvisTarget;

                spine._pelvisPositionWeight = data._pelvisPositionWeight;
                spine._pelvisRotationWeight = data._pelvisRotationWeight;
                spine._maxRootAngle = 180f;

                ik.Solver._plantFeet = false;
            }
            else if (leftFootTracker != null && rightFootTracker != null)
            {
                spine._maxRootAngle = 0f;
            }

            // Left Hand
            var leftArm = ik.Solver._leftArm;
            if (leftHandTracker != null)
            {
                Transform leftHandTarget = leftArm._target ?? (new SceneNode(root.World, "Left Hand Target")).GetTransformAs<Transform>(true)!;

                leftHandTarget.SetParent(leftHandTracker, false);
                data._leftHand?.ApplyLocalTransformTo(leftHandTarget);

                leftArm._target = leftHandTarget;
                leftArm._positionWeight = 1f;
                leftArm._rotationWeight = 1f;
            }
            else
            {
                leftArm._positionWeight = 0f;
                leftArm._rotationWeight = 0f;
            }

            // Right Hand
            var rightArm = ik.Solver._rightArm;
            if (rightHandTracker != null)
            {
                Transform rightHandTarget = rightArm._target ?? (new SceneNode(root.World, "Right Hand Target")).GetTransformAs<Transform>(true)!;

                rightHandTarget.SetParent(rightHandTracker, false);
                data._rightHand?.ApplyLocalTransformTo(rightHandTarget);

                rightArm._target = rightHandTarget;
                rightArm._positionWeight = 1f;
                rightArm._rotationWeight = 1f;
            }
            else
            {
                rightArm._positionWeight = 0f;
                rightArm._rotationWeight = 0f;
            }

            // Legs
            if (leftFootTracker != null)
            {
                Transform? lastBone = (ik.Humanoid?.Left.Toes.Node ?? ik.Humanoid?.Left.Foot.Node)?.GetTransformAs<Transform>(true);
                CalibrateLeg(
                    data,
                    leftFootTracker,
                    ik.Solver._leftLeg,
                    lastBone,
                    root.WorldForward,
                    true);
            }

            if (rightFootTracker != null)
            {
                Transform? lastBone = (ik.Humanoid?.Right.Toes.Node ?? ik.Humanoid?.Right.Foot.Node)?.GetTransformAs<Transform>(true);
                CalibrateLeg(
                    data,
                    rightFootTracker,
                    ik.Solver._rightLeg,
                    lastBone,
                    root.WorldForward,
                    false);
            }

            // Additional solver settings
            spine._minHeadHeight = 0f;
            ik.Solver._locomotion._weight = bodyTracker == null && leftFootTracker == null && rightFootTracker == null ? 1f : 0f;

            if (root.SceneNode is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without a root transform.");
                return;
            }

            var rootController = root.SceneNode.GetComponent<VRIKRootControllerComponent>();

            // Root controller
            bool addRootController = bodyTracker != null || (leftFootTracker != null && rightFootTracker != null);
            if (addRootController)
            {
                rootController ??= root.SceneNode.AddComponent<VRIKRootControllerComponent>()!;
                rootController.Calibrate(data);
            }
            else
                rootController?.Destroy();
        }

        private static void CalibrateLeg(CalibrationData data, Transform tracker, IKSolverVR.Leg leg, Transform? lastBone, Vector3 rootForward, bool isLeft)
        {
            if (isLeft && data._leftFoot is null) 
                return;

            if (!isLeft && data._rightFoot is null)
                return;

            string name = isLeft ? "Left" : "Right";

            Transform target = leg._target ?? new SceneNode(tracker.World, $"{name} Foot Target").GetTransformAs<Transform>(true)!;

            target.SetParent(tracker, true);

            if (isLeft)
                data._leftFoot?.ApplyLocalTransformTo(target);
            else
                data._rightFoot?.ApplyLocalTransformTo(target);

            leg._target = target;

            leg._positionWeight = 1f;
            leg._rotationWeight = 1f;

            // Bend goal
            Transform bendGoal = leg._bendGoal ?? new SceneNode(tracker.World, $"{name} Leg Bend Goal").GetTransformAs<Transform>(true)!;
            bendGoal.SetParent(tracker, true);

            if (isLeft)
                data._leftLegGoal?.ApplyLocalTransformTo(bendGoal);
            else
                data._rightLegGoal?.ApplyLocalTransformTo(bendGoal);

            leg._bendGoal = bendGoal;
            leg._bendGoalWeight = 1f;
        }

        /// <summary>
        /// Simple calibration to head and hands using predefined anchor position and rotation offsets.
        /// </summary>
        /// <param name="ik">The VRIK component.</param>
        /// <param name="centerEyeAnchor">HMD.</param>
        /// <param name="leftHandAnchor">Left hand controller.</param>
        /// <param name="rightHandAnchor">Right hand controller.</param>
        /// <param name="centerEyePositionOffset">Position offset of the camera from the head bone (root space).</param>
        /// <param name="centerEyeRotationOffset">Rotation offset of the camera from the head bone (root space).</param>
        /// <param name="handPositionOffset">Position offset of the hand controller from the hand bone (controller space).</param>
        /// <param name="handRotationOffset">Rotation offset of the hand controller from the hand bone (controller space).</param>
        /// <param name="scaleMlp">Multiplies the scale of the root.</param>
        /// <returns></returns>
        public static CalibrationData Calibrate(VRIKSolverComponent ik, Transform centerEyeAnchor, Transform leftHandAnchor, Transform rightHandAnchor, Vector3 centerEyePositionOffset, Vector3 centerEyeRotationOffset, Vector3 handPositionOffset, Vector3 handRotationOffset, float scaleMlp = 1f)
        {
            CalibrateHead(ik, centerEyeAnchor, centerEyePositionOffset, centerEyeRotationOffset);
            CalibrateHands(ik, leftHandAnchor, rightHandAnchor, handPositionOffset, handRotationOffset);
            CalibrateScale(ik, scaleMlp);
            return new()
            {
                _scale = ik.Root?.Scale.Y ?? 1.0f,
                _head = new CalibrationData.Target(ik.Solver._spine._headTarget),
                _leftHand = new CalibrationData.Target(ik.Solver._leftArm._target),
                _rightHand = new CalibrationData.Target(ik.Solver._rightArm._target)
            };
        }

        /// <summary>
        /// Calibrates head IK target to specified anchor position and rotation offset independent of avatar bone orientations.
        /// </summary>
        public static void CalibrateHead(VRIKSolverComponent ik, Transform centerEyeAnchor, Vector3 anchorPositionOffset, Vector3 anchorRotationOffset)
        {
            var spine = ik.Solver._spine;
            spine._headTarget ??= new SceneNode("Head IK Target").GetTransformAs<Transform>(true)!;

            var root = ik.Root;
            if (root is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the root transform.");
                return;
            }
            var head = ik.Humanoid?.Head.Node?.GetTransformAs<Transform>(true);
            if (head is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the head transform.");
                return;
            }

            Vector3 forward = Quaternion.Inverse(head.WorldRotation).Rotate(root.WorldForward);
            Vector3 up = Quaternion.Inverse(head.WorldRotation).Rotate(root.WorldUp);
            Quaternion headSpace = XRMath.LookRotation(forward, up);

            Vector3 anchorPos = head.WorldTranslation + (head.WorldRotation * headSpace).Rotate(anchorPositionOffset);
            Quaternion anchorRot = head.WorldRotation * headSpace * Quaternion.CreateFromYawPitchRoll(anchorRotationOffset.Y, anchorRotationOffset.X, anchorRotationOffset.Z);
            Quaternion anchorRotInverse = Quaternion.Inverse(anchorRot);

            spine._headTarget.SetParent(centerEyeAnchor, false);
            spine._headTarget.Translation = anchorRotInverse.Rotate(head.WorldTranslation - anchorPos);
            spine._headTarget.Rotation = anchorRotInverse * head.WorldRotation;
        }

        /// <summary>
        /// Calibrates body target to avatar pelvis position and position/rotation offsets in character root space.
        /// </summary>
        public static void CalibrateBody(VRIKSolverComponent ik, Transform hipsTracker, Vector3 trackerPositionOffset, Vector3 trackerRotationOffset)
        {
            var spine = ik.Solver._spine;
            var root = ik.Root;
            if (root is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the root transform.");
                return;
            }
            var hips = ik.Humanoid?.Hips.Node?.GetTransformAs<Transform>(true);
            if (hips is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the hips transform.");
                return;
            }
            spine._hipsTarget ??= new SceneNode(ik.World, "Hips IK Target").GetTransformAs<Transform>(true)!;
            spine._hipsTarget.SetParent(hipsTracker, false);
            spine._hipsTarget.SetWorldTranslation(hips.WorldTranslation + root.WorldRotation.Rotate(trackerPositionOffset));
            spine._hipsTarget.SetWorldRotation(root.WorldRotation * Quaternion.CreateFromYawPitchRoll(trackerRotationOffset.Y, trackerPositionOffset.X, trackerPositionOffset.Z));
        }

        /// <summary>
        /// Calibrates hand IK targets to specified anchor position and rotation offsets independent of avatar bone orientations.
        /// </summary>
        public static void CalibrateHands(VRIKSolverComponent ik, Transform leftHandAnchor, Transform rightHandAnchor, Vector3 anchorPositionOffset, Vector3 anchorRotationOffset)
        {
            ik.Solver._leftArm._target ??= new SceneNode(ik.World, "Left Hand IK Target").GetTransformAs<Transform>(true)!;
            ik.Solver._rightArm._target ??= new SceneNode(ik.World, "Right Hand IK Target").GetTransformAs<Transform>(true)!;

            CalibrateHand(ik, leftHandAnchor, anchorPositionOffset, anchorRotationOffset, true);
            CalibrateHand(ik, rightHandAnchor, anchorPositionOffset, anchorRotationOffset, false);
        }

        private static void CalibrateHand(VRIKSolverComponent ik, Transform anchor, Vector3 positionOffset, Vector3 rotationOffset, bool isLeft)
        {
            if (isLeft)
            {
                positionOffset.X = -positionOffset.X;
                rotationOffset.Y = -rotationOffset.Y;
                rotationOffset.Z = -rotationOffset.Z;
            }

            var human = ik.Humanoid;
            if (human is null)
            {
                Debug.LogWarning("Can not calibrate VRIK without the humanoid component.");
                return;
            }

            var hand = (isLeft ? human.Left.Wrist.Node : human.Right.Wrist.Node)?.GetTransformAs<Transform>(true);
            var forearm = (isLeft ? human.Left.Elbow.Node : human.Right.Elbow.Node)?.GetTransformAs<Transform>(true);
            var target = isLeft ? ik.Solver._leftArm._target : ik.Solver._rightArm._target;

            Vector3 forward = isLeft
                ? ik.Solver._leftArm._wristToPalmAxis
                : ik.Solver._rightArm._wristToPalmAxis;

            if (forward == Vector3.Zero)
                forward = GuessWristToPalmAxis(hand, forearm);

            Vector3 up = isLeft
                ? ik.Solver._leftArm._palmToThumbAxis
                : ik.Solver._rightArm._palmToThumbAxis;

            if (up == Vector3.Zero)
                up = GuessPalmToThumbAxis(hand, forearm);

            Quaternion handSpace = XRMath.LookRotation(forward, up);
            Vector3 anchorPos = hand.WorldTranslation + (hand.WorldRotation * handSpace).Rotate(positionOffset);
            Quaternion anchorRot = hand.WorldRotation * handSpace * Quaternion.CreateFromYawPitchRoll(rotationOffset.Y, rotationOffset.X, rotationOffset.Z);
            Quaternion anchorRotInverse = Quaternion.Inverse(anchorRot);

            target.SetParent(anchor, false);
            target.Translation = anchorRotInverse.Rotate(hand.WorldTranslation - anchorPos);
            target.Rotation = anchorRotInverse * hand.WorldRotation;
        }

        public static Vector3 GuessWristToPalmAxis(Transform? hand, Transform? forearm)
        {
            if (hand is null || forearm is null)
            {
                Debug.LogWarning("Can not guess the hand bone's orientation without the hand and forearm transforms.");
                return Vector3.Zero;
            }
            Vector3 toForearm = forearm.WorldTranslation - hand.WorldTranslation;
            Vector3 axis = XRMath.AxisToVector(XRMath.GetAxisToDirection(hand.WorldRotation, toForearm));
            if (Vector3.Dot(toForearm, hand.WorldRotation.Rotate(axis)) > 0f)
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
            if (Vector3.Dot(toThumb, hand.WorldRotation.Rotate(axis)) < 0f)
                axis = -axis;
            return axis;
        }
    }
}
