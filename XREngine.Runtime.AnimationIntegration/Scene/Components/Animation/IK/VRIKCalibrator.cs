using System.Numerics;
using XREngine.Extensions;
using XREngine.Input;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>Validates and captures device offsets without changing the avatar's measured scale.</summary>
    public static partial class VRIKCalibrator
    {
        public static CalibrationData? Calibrate(VRIKSolverComponent ik, VRIKCalibrationSettings settings,
            TransformBase? headTracker, TransformBase? bodyTracker = null, TransformBase? leftHandTracker = null,
            TransformBase? rightHandTracker = null, TransformBase? leftFootTracker = null, TransformBase? rightFootTracker = null)
            => TryCalibrate(ik, settings, headTracker, bodyTracker, leftHandTracker, rightHandTracker, leftFootTracker, rightFootTracker).Data as CalibrationData;

        /// <summary>Failure leaves existing targets, solver weights and avatar scale unchanged.</summary>
        public static VrCalibrationResult TryCalibrate(VRIKSolverComponent ik, VRIKCalibrationSettings? settings,
            TransformBase? headTracker, TransformBase? bodyTracker = null, TransformBase? leftHandTracker = null,
            TransformBase? rightHandTracker = null, TransformBase? leftFootTracker = null, TransformBase? rightFootTracker = null)
            => CalibrateCore(ik, settings, headTracker, bodyTracker, leftHandTracker, rightHandTracker, leftFootTracker, rightFootTracker, null, null);

        public static VrCalibrationResult TryCalibrateSnapshot(VRIKSolverComponent ik, VRIKCalibrationSettings? settings, VrCalibrationPose[] poses, float headTiltToleranceDegrees)
            => CalibrateCore(ik, settings, ik.HeadTarget.tfm, ik.HipsTarget.tfm, ik.LeftHandTarget.tfm,
                ik.RightHandTarget.tfm, ik.LeftFootTarget.tfm, ik.RightFootTarget.tfm, poses, headTiltToleranceDegrees);

        private static VrCalibrationResult CalibrateCore(VRIKSolverComponent ik, VRIKCalibrationSettings? settings,
            TransformBase? headTracker, TransformBase? bodyTracker, TransformBase? leftHandTracker,
            TransformBase? rightHandTracker, TransformBase? leftFootTracker, TransformBase? rightFootTracker, VrCalibrationPose[]? poses,
            float? headTiltToleranceDegrees)
        {
            if (settings is null)
                return VrCalibrationResult.Failure("Calibration settings are unavailable.");
            if (!float.IsFinite(settings.HipPositionWeight) || settings.HipPositionWeight is < 0 or > 1
                || !float.IsFinite(settings.HipRotationWeight) || settings.HipRotationWeight is < 0 or > 1)
                return VrCalibrationResult.Failure("Calibration weights must be finite and between zero and one.");
            if (!ik.Solver.Initialized || ik.Root is null || ik.Humanoid.Head.Node is null)
                return VrCalibrationResult.Failure("The avatar's IK rig is not ready.");
            TransformBase?[] sources = [headTracker, bodyTracker, leftHandTracker, rightHandTracker, leftFootTracker, rightFootTracker,
                ik.Humanoid.GetIKTargetTransform(EHumanoidIKTarget.LeftElbow), ik.Humanoid.GetIKTargetTransform(EHumanoidIKTarget.RightElbow),
                ik.Humanoid.GetIKTargetTransform(EHumanoidIKTarget.LeftKnee), ik.Humanoid.GetIKTargetTransform(EHumanoidIKTarget.RightKnee),
                ik.Humanoid.GetIKTargetTransform(EHumanoidIKTarget.Chest)];
            var proposals = new List<VrCalibrationTarget>(11);
            for (int i = 0; i < sources.Length; i++)
            {
                EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
                TransformBase? source = ik.GetCalibrationSource(slot, sources[i]);
                if (source is null)
                {
                    if (slot == EHumanoidIKTarget.Head)
                        return VrCalibrationResult.Failure("The headset is not tracking.");
                    continue;
                }
                if (source.SceneNode is null || source.IsDestroyed || source.IsDestroyQueued
                    || source is IVrTrackingPoseSource { PoseCurrentlyUsable: false })
                    return VrCalibrationResult.Failure(slot + " is not currently tracking.");
                Matrix4x4 deviceWorld;
                if (poses is null)
                {
                    source.RecalculateMatrices(true);
                    deviceWorld = source.WorldMatrix;
                }
                else
                {
                    bool found = false;
                    deviceWorld = default;
                    foreach (VrCalibrationPose pose in poses)
                        if (ReferenceEquals(pose.Source, source))
                        {
                            deviceWorld = pose.WorldPose;
                            found = true;
                            break;
                        }
                    if (!found)
                        return VrCalibrationResult.Failure(slot + " was not part of the capture snapshot.");
                }
                if (!VrCalibrationMath.TryGetRigidPose(deviceWorld, out _))
                    return VrCalibrationResult.Failure(slot + " has an invalid tracking pose.");
                if (poses is not null && slot == EHumanoidIKTarget.Head && !VrCalibrationMath.IsHeadLevel(deviceWorld, headTiltToleranceDegrees ?? settings.HeadTiltToleranceDegrees))
                    return VrCalibrationResult.Failure("Look straight ahead and keep your head level, then pull both triggers again.");
                Matrix4x4 targetWorld;
                if (slot == EHumanoidIKTarget.Head)
                {
                    // Prefer a measured eye-to-head offset; otherwise use the avatar's bind anatomy.
                    Matrix4x4 fixedOffset = ik.GetFixedCalibrationOffset(slot, source, Matrix4x4.Identity);
                    if (fixedOffset == Matrix4x4.Identity && !TryGetBindHeadOffset(ik.Humanoid, out fixedOffset))
                        return VrCalibrationResult.Failure("The avatar head bind pose or body facing basis is unavailable.");
                    targetWorld = fixedOffset * deviceWorld;
                }
                else if (slot is EHumanoidIKTarget.LeftHand or EHumanoidIKTarget.RightHand)
                {
                    bool left = slot == EHumanoidIKTarget.LeftHand;
                    var arm = left ? ik.Solver.LeftArm : ik.Solver.RightArm;
                    Quaternion look = XRMath.LookRotation(settings.HandTrackerForward, settings.HandTrackerUp);
                    Quaternion rotation = XRMath.MatchRotation(look, settings.HandTrackerForward, settings.HandTrackerUp,
                        arm.WristToPalmAxis, (left ? 1.0f : -1.0f) * Vector3.Cross(arm.WristToPalmAxis, arm.PalmToThumbAxis));
                    Matrix4x4 preset = ik.GetFixedCalibrationOffset(slot, source,
                        Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(
                            source is IVrControllerPoseSource controller ? controller.GripToWristOffset : settings.HandOffset));
                    targetWorld = preset * deviceWorld;
                }
                else
                {
                    TransformBase? bone = GetSlotBone(ik.Humanoid, slot);
                    if (bone is null)
                        return VrCalibrationResult.Failure("The avatar has no bone for " + slot + ".");
                    bone.RecalculateMatrices(true);
                    targetWorld = bone.WorldMatrix;
                }
                if (!VrCalibrationMath.TryCaptureOffset(targetWorld, deviceWorld, out Matrix4x4 offset))
                    return VrCalibrationResult.Failure(slot + " has a singular or non-finite capture transform.");
                proposals.Add(new(slot, source, offset));
            }
            try
            {
                ik.CommitCalibrationTargets(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(proposals), () => ApplyWeights(ik, settings));
            }
            catch (Exception exception)
            {
                return VrCalibrationResult.Failure("Calibration could not be committed: " + exception.Message);
            }
            if (poses is not null)
            {
                // The standing pose becomes the solver's reset pose only after a successful capture.
                try { ik.Solver.StoreDefaultLocalState(); }
                catch (Exception exception) { Debug.Animation("Calibration reset pose could not be stored: " + exception.Message); }
            }
            var spine = ik.Solver.Spine;
            return VrCalibrationResult.Completed(new CalibrationData
            {
                Scale = ik.Root.Scale.Y,
                Head = new(spine.HeadTarget), Hips = new(spine.HipsTarget),
                LeftHand = new(ik.Solver.LeftArm.Target), RightHand = new(ik.Solver.RightArm.Target),
                LeftFoot = new(ik.Solver.LeftLeg.Target), RightFoot = new(ik.Solver.RightLeg.Target),
                LeftLegGoal = new(ik.Solver.LeftLeg.KneeTarget), RightLegGoal = new(ik.Solver.RightLeg.KneeTarget),
                HipsPositionWeight = spine.HipsPositionWeight, HipsRotationWeight = spine.HipsRotationWeight,
            });
        }

        private static TransformBase? GetSlotBone(HumanoidComponent human, EHumanoidIKTarget slot)
            => slot switch
            {
                EHumanoidIKTarget.Hips => human.Hips.Node?.Transform,
                EHumanoidIKTarget.Chest => (human.Chest.Node ?? human.Spine.Node)?.Transform,
                EHumanoidIKTarget.LeftFoot => (human.Left.Toes.Node ?? human.Left.Foot.Node)?.Transform,
                EHumanoidIKTarget.RightFoot => (human.Right.Toes.Node ?? human.Right.Foot.Node)?.Transform,
                EHumanoidIKTarget.LeftElbow => human.Left.Arm.Node?.Transform,
                EHumanoidIKTarget.RightElbow => human.Right.Arm.Node?.Transform,
                EHumanoidIKTarget.LeftKnee => human.Left.Knee.Node?.Transform,
                EHumanoidIKTarget.RightKnee => human.Right.Knee.Node?.Transform,
                _ => null,
            };

        private static bool TryGetBindHeadOffset(HumanoidComponent human, out Matrix4x4 offset)
        {
            offset = Matrix4x4.Identity;
            if (human.Head.Node is null || !human.TryGetVrBindBodyToEngine(out Matrix4x4 bodyToEngine))
                return false;
            Matrix4x4 headInRoot = human.Head.Node.Transform.BindMatrix * human.Transform.InverseBindMatrix;
            if (!Matrix4x4.Decompose(headInRoot, out _, out Quaternion headRotation, out Vector3 headPosition))
                return false;
            TransformBase eyes = human.EyesTarget.Node?.Transform ?? human.Head.Node.Transform;
            Matrix4x4 eyesInRoot = eyes.BindMatrix * human.Transform.InverseBindMatrix;
            if (!Matrix4x4.Decompose(human.Transform.WorldMatrix, out Vector3 rootScale, out _, out _))
                return false;
            Vector3 eyeToHead = Vector3.Multiply(headPosition - eyesInRoot.Translation, rootScale);
            offset = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(headRotation)) * bodyToEngine;
            offset.Translation = Vector3.TransformNormal(eyeToHead, bodyToEngine);
            return VrCalibrationMath.TryGetRigidPose(offset, out _);
        }

        internal static void ApplyWeights(VRIKSolverComponent ik, VRIKCalibrationSettings settings)
        {
            var solver = ik.Solver;
            ik.RestoreConfiguredHeadWeights();
            solver.Spine.HipsPositionWeight = solver.Spine.HipsTarget is null ? 0 : settings.HipPositionWeight;
            solver.Spine.HipsRotationWeight = solver.Spine.HipsTarget is null ? 0 : settings.HipRotationWeight;
            solver.Spine.ChestTargetWeight = solver.Spine.ChestTarget is null ? 0 : 1;
            solver.LeftArm.Settings.PositionWeight = solver.LeftArm.Settings.RotationWeight = solver.LeftArm.Target is null ? 0 : 1;
            solver.RightArm.Settings.PositionWeight = solver.RightArm.Settings.RotationWeight = solver.RightArm.Target is null ? 0 : 1;
            solver.LeftArm.Settings.UpperArmTargetWeight = solver.LeftArm.UpperArmTarget is null ? 0 : 1;
            solver.RightArm.Settings.UpperArmTargetWeight = solver.RightArm.UpperArmTarget is null ? 0 : 1;
            solver.LeftLeg.PositionWeight = solver.LeftLeg.RotationWeight = solver.LeftLeg.Target is null ? 0 : 1;
            solver.RightLeg.PositionWeight = solver.RightLeg.RotationWeight = solver.RightLeg.Target is null ? 0 : 1;
            solver.LeftLeg.KneeTargetWeight = solver.LeftLeg.KneeTarget is null ? 0 : 1;
            solver.RightLeg.KneeTargetWeight = solver.RightLeg.KneeTarget is null ? 0 : 1;
            solver.PlantFeet = false;
        }

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
