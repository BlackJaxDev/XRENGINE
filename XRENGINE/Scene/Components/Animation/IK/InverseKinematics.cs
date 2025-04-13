using Extensions;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    public static partial class InverseKinematics
    {
        /// <summary>
        /// Solves a full body IK chain with only required targets for each bone.
        /// </summary>
        /// <param name="hipToHead"></param>
        /// <param name="leftLegToAnkle"></param>
        /// <param name="rightLegToAnkle"></param>
        /// <param name="leftShoulderToWrist"></param>
        /// <param name="rightShoulderToWrist"></param>
        /// <param name="HeadTarget"></param>
        /// <param name="HipsTarget"></param>
        /// <param name="LeftHandTarget"></param>
        /// <param name="RightHandTarget"></param>
        /// <param name="LeftFootTarget"></param>
        /// <param name="RightFootTarget"></param>
        /// <param name="maxIterations"></param>
        public static void SolveFullBodyIK(
            BoneChainItem[]? hipToHead,
            BoneChainItem[]? leftLegToAnkle,
            BoneChainItem[]? rightLegToAnkle,
            BoneChainItem[]? leftShoulderToWrist,
            BoneChainItem[]? rightShoulderToWrist,
            (TransformBase? tfm, Matrix4x4 offset) HeadTarget,
            (TransformBase? tfm, Matrix4x4 offset) HipsTarget,
            (TransformBase? tfm, Matrix4x4 offset) LeftHandTarget,
            (TransformBase? tfm, Matrix4x4 offset) RightHandTarget,
            (TransformBase? tfm, Matrix4x4 offset) LeftFootTarget,
            (TransformBase? tfm, Matrix4x4 offset) RightFootTarget,
            int maxIterations = 10)
            => SolveFullBodyIK(
                hipToHead,
                leftLegToAnkle,
                rightLegToAnkle,
                leftShoulderToWrist,
                rightShoulderToWrist,
                HeadTarget,
                HipsTarget,
                LeftHandTarget,
                RightHandTarget,
                LeftFootTarget,
                RightFootTarget,
                (null, Matrix4x4.Identity),
                (null, Matrix4x4.Identity),
                (null, Matrix4x4.Identity),
                (null, Matrix4x4.Identity),
                (null, Matrix4x4.Identity),
                maxIterations);

        /// <summary>
        /// Solves a full body IK chain with all optional targets for each bone.
        /// </summary>
        /// <param name="hipToHead"></param>
        /// <param name="leftLegToAnkle"></param>
        /// <param name="rightLegToAnkle"></param>
        /// <param name="leftShoulderToWrist"></param>
        /// <param name="rightShoulderToWrist"></param>
        /// <param name="HeadTarget"></param>
        /// <param name="HipsTarget"></param>
        /// <param name="LeftHandTarget"></param>
        /// <param name="RightHandTarget"></param>
        /// <param name="LeftFootTarget"></param>
        /// <param name="RightFootTarget"></param>
        /// <param name="LeftElbowTarget"></param>
        /// <param name="RightElbowTarget"></param>
        /// <param name="LeftKneeTarget"></param>
        /// <param name="RightKneeTarget"></param>
        /// <param name="ChestTarget"></param>
        /// <param name="maxIterations"></param>
        public static void SolveFullBodyIK(
            BoneChainItem[]? hipToHead,
            BoneChainItem[]? leftLegToAnkle,
            BoneChainItem[]? rightLegToAnkle,
            BoneChainItem[]? leftShoulderToWrist,
            BoneChainItem[]? rightShoulderToWrist,
            (TransformBase? tfm, Matrix4x4 offset) HeadTarget,
            (TransformBase? tfm, Matrix4x4 offset) HipsTarget,
            (TransformBase? tfm, Matrix4x4 offset) LeftHandTarget,
            (TransformBase? tfm, Matrix4x4 offset) RightHandTarget,
            (TransformBase? tfm, Matrix4x4 offset) LeftFootTarget,
            (TransformBase? tfm, Matrix4x4 offset) RightFootTarget,
            (TransformBase? tfm, Matrix4x4 offset) LeftElbowTarget, //optional
            (TransformBase? tfm, Matrix4x4 offset) RightElbowTarget, //optional
            (TransformBase? tfm, Matrix4x4 offset) LeftKneeTarget, //optional
            (TransformBase? tfm, Matrix4x4 offset) RightKneeTarget, //optional
            (TransformBase? tfm, Matrix4x4 offset) ChestTarget, //optional
            int maxIterations = 10)
        {
            if (maxIterations < 1)
                maxIterations = 1;

            //TODO: if a target is missing, use a default transform relative to another instead of using identity

            //Transforms we're solving for, offset included (ex; tracker transform to actual hip transform set during calibration)
            Matrix4x4 hipTarget = HipsTarget.offset * (HipsTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 headTarget = HeadTarget.offset * (HeadTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 leftHandTarget = LeftHandTarget.offset * (LeftHandTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 rightHandTarget = RightHandTarget.offset * (RightHandTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 leftFootTarget = LeftFootTarget.offset * (LeftFootTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 rightFootTarget = RightFootTarget.offset * (RightFootTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);

            //TODO: utilize optional targets to solves for each chain, if they exist
            Matrix4x4 leftElbowTarget = LeftElbowTarget.offset * (LeftElbowTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 rightElbowTarget = RightElbowTarget.offset * (RightElbowTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 leftKneeTarget = LeftKneeTarget.offset * (LeftKneeTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 rightKneeTarget = RightKneeTarget.offset * (RightKneeTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);
            Matrix4x4 chestTarget = ChestTarget.offset * (ChestTarget.tfm?.WorldMatrix ?? Matrix4x4.Identity);

            //Hips to Head (both hips and head are targets)
            if (hipToHead is not null)
                //SolveDoubleEndedTargets(hipToHead, hipTarget, headTarget, 0.001f, maxIterations);
                SolveSingleTarget(hipToHead, headTarget, 0.001f, maxIterations);

            //Left Shoulder to Wrist
            if (leftShoulderToWrist is not null)
                SolveSingleTarget(leftShoulderToWrist, leftHandTarget, 0.001f, maxIterations);

            //Right Shoulder to Wrist
            if (rightShoulderToWrist is not null)
                SolveSingleTarget(rightShoulderToWrist, rightHandTarget, 0.001f, maxIterations);

            //Left Leg to Ankle
            if (leftLegToAnkle is not null)
                SolveSingleTarget(leftLegToAnkle, leftFootTarget, 0.001f, maxIterations);

            //Right Leg to Ankle
            if (rightLegToAnkle is not null)
                SolveSingleTarget(rightLegToAnkle, rightFootTarget, 0.001f, maxIterations);
        }

        /// <summary>
        /// Solves a chain of bones to reach a single target.
        /// The chain must have at least two bones,
        /// and the last bone in the chain will be moved to the target.
        /// </summary>
        /// <param name="chain"></param>
        /// <param name="target"></param>
        /// <param name="tolerance"></param>
        /// <param name="maxIterations"></param>
        public static void SolveSingleTarget(
            BoneChainItem[] chain,
            Matrix4x4 target,
            float tolerance = 0.001f,
            int maxIterations = 10)
        {
            if (chain.Length < 2)
            {
                Debug.LogWarning("The chain must have at least two bones.");
                return;
            }

            //Decompose the target matrix to get the position and rotation. Scale is ignored.
            //TODO: use target rotation
            Matrix4x4.Decompose(target, out _, out Quaternion targetRot, out Vector3 targetPos);

            //Verify for NaN
            if (float.IsNaN(targetPos.X) || float.IsNaN(targetPos.Y) || float.IsNaN(targetPos.Z))
                return;
            if (float.IsNaN(targetRot.X) || float.IsNaN(targetRot.Y) || float.IsNaN(targetRot.Z) || float.IsNaN(targetRot.W))
                return;

            float totalLength = PreSolve(chain);
            Vector3 originalRootPosition = chain[0].WorldPosSolve;
            float distanceToTarget = Vector3.DistanceSquared(originalRootPosition, targetPos);

            //if (distanceToTarget > totalLength * totalLength)
            //{
            //    //Easy case: the target is out of reach, all bones will look at the target
            //    StretchChain(chain, originalRootPosition, targetPos);
            //}
            //else
            {
                int iterations = 0;
                float diff = Vector3.Distance(chain[^1].WorldPosSolve, targetPos);
                while (diff > tolerance && iterations < maxIterations)
                {
                    // **Forward Reaching Phase**
                    //Move end bone to the target
                    chain[^1].WorldPosSolve = targetPos;
                    //Iterate backwards through the chain
                    for (int i = chain.Length - 1; i > 0; i--)
                    {
                        BoneChainItem parent = chain[i - 1];
                        BoneChainItem child = chain[i];
                        ForwardPhaseSolveWorld(parent, child);
                        //Constrain(parent, child);
                    }

                    // **Backward Reaching Phase**
                    // Move the root back to its original position
                    chain[0].WorldPosSolve = originalRootPosition;
                    //Iterate forward through the chain
                    for (int i = 0; i < chain.Length - 1; i++)
                    {
                        BoneChainItem parent = chain[i];
                        BoneChainItem child = chain[i + 1];
                        BackwardPhaseSolveWorld(parent, child);
                        //Constrain(parent, child);
                    }

                    //Update the diff and iteration count for convergence
                    diff = Vector3.Distance(chain[^1].WorldPosSolve, targetPos);
                    iterations++;
                }
            }

            //Apply world positions to transforms and calculate rotations
            PostSolve(chain);
        }

        public static void SolveDoubleEndedTargets(
            BoneChainItem[] chain,
            Matrix4x4 startTarget,
            Matrix4x4 endTarget,
            float tolerance = 0.001f,
            int maxIterations = 10)
        {
            int numBones = chain.Length;
            if (numBones < 2)
            {
                Debug.LogWarning("The chain must have at least two bones.");
                return;
            }

            Matrix4x4.Decompose(startTarget, out _, out Quaternion startTargetRot, out Vector3 startTargetPos);
            Matrix4x4.Decompose(endTarget, out _, out Quaternion endTargetRot, out Vector3 endTargetPos);

            if (float.IsNaN(startTargetPos.X) || float.IsNaN(startTargetPos.Y) || float.IsNaN(startTargetPos.Z))
                return;
            if (float.IsNaN(endTargetPos.X) || float.IsNaN(endTargetPos.Y) || float.IsNaN(endTargetPos.Z))
                return;
            if (float.IsNaN(startTargetRot.X) || float.IsNaN(startTargetRot.Y) || float.IsNaN(startTargetRot.Z) || float.IsNaN(startTargetRot.W))
                return;
            if (float.IsNaN(endTargetRot.X) || float.IsNaN(endTargetRot.Y) || float.IsNaN(endTargetRot.Z) || float.IsNaN(endTargetRot.W))
                return;

            float totalLength = PreSolve(chain);
            float distanceBetweenTargets = Vector3.DistanceSquared(startTargetPos, endTargetPos);

            // Distance between the fixed points
            if (distanceBetweenTargets > totalLength * totalLength)
            {
                //Easy case: the targets are out of reach of each other,
                //Stretch the end points away from their positions to reach each other
                float distDiff = MathF.Sqrt(distanceBetweenTargets) - totalLength;
                float halfDiff = distDiff * 0.5f;

                Vector3 startToEndDir = (endTargetPos - startTargetPos).Normalized();
                Vector3 halfDiffVec = startToEndDir * halfDiff;

                Vector3 startPos = startTargetPos + halfDiffVec;
                Vector3 endPos = endTargetPos - halfDiffVec;

                //StretchChain(chain, startPos, endPos);
            }
            else
            {
                int iterations = 0;
                float startDiff = Vector3.Distance(chain[0].WorldPosSolve, startTargetPos);
                float endDiff = Vector3.Distance(chain[^1].WorldPosSolve, endTargetPos);
                while (startDiff > tolerance && endDiff > tolerance && iterations < maxIterations)
                {
                    // **Forward Reaching Phase**
                    //Move end bone to the target
                    chain[^1].WorldPosSolve = endTargetPos;
                    //Iterate backwards through the chain
                    for (int i = chain.Length - 1; i > 0; i--)
                    {
                        BoneChainItem parent = chain[i - 1];
                        BoneChainItem child = chain[i];
                        ForwardPhaseSolveWorld(parent, child);
                        //Constrain(parent, child);
                    }

                    // **Backward Reaching Phase**
                    // Move the root back to its original position
                    chain[0].WorldPosSolve = startTargetPos;
                    //Iterate forward through the chain
                    for (int i = 0; i < chain.Length - 1; i++)
                    {
                        BoneChainItem parent = chain[i];
                        BoneChainItem child = chain[i + 1];
                        BackwardPhaseSolveWorld(parent, child);
                        //Constrain(parent, child);
                    }

                    //Update the diff and iteration count for convergence
                    startDiff = Vector3.Distance(chain[0].WorldPosSolve, startTargetPos);
                    endDiff = Vector3.Distance(chain[^1].WorldPosSolve, endTargetPos);
                    iterations++;
                }
            }

            //Apply world positions to transforms and calculate rotations
            PostSolve(chain);
        }

        //private static void StretchChain(BoneChainItem[] chain, Vector3 startPos, Vector3 endPos)
        //{
        //    var root = chain[0];
        //    root.WorldPosSolve = startPos;
        //    root.WorldChildDirSolve = (endPos - root.WorldPosSolve).Normalized();
        //    for (int i = 1; i < chain.Length; i++)
        //    {
        //        var bone = chain[i];
        //        var prevBone = chain[i - 1];
        //        float dist = prevBone.DistanceToChild;
        //        bone.WorldPosSolve = prevBone.WorldPosSolve + prevBone.WorldChildDirSolve * dist;
        //        bone.WorldChildDirSolve = (endPos - bone.WorldPosSolve).Normalized();
        //    }
        //}

        private static void BackwardPhaseSolveWorld(BoneChainItem parent, BoneChainItem child)
        {
            //Get the direction from the parent to the child
            //Child world pos solve was set during the forward phase
            Vector3 parentToChildDir = (child.WorldPosSolve - parent.WorldPosSolve).Normalized();
            //Get the vector from the parent to the child with the original distance instead of the current distance, which was normalized away
            Vector3 parentToChild = parentToChildDir * parent.DistanceToChild;
            //Set the child's world position to the parent's world position plus the original distance
            child.WorldPosSolve = parent.WorldPosSolve + parentToChild;
            parent.WorldChildDirSolve = parentToChildDir;
        }

        private static void ForwardPhaseSolveWorld(BoneChainItem parent, BoneChainItem child)
        {
            //Get the direction from the parent to the child.
            //Parent world pos solve is from the previous iteration
            Vector3 parentToChildDir = (child.WorldPosSolve - parent.WorldPosSolve).Normalized();
            //Get the vector from the parent to the child with the original distance instead of the current distance, which was normalized away
            Vector3 parentToChild = parentToChildDir * parent.DistanceToChild;
            //Set the parent's world position to the child's world position minus the original distance
            parent.WorldPosSolve = child.WorldPosSolve - parentToChild;
            parent.WorldChildDirSolve = parentToChildDir;
        }

        //private static void Constrain(BoneChainItem parent, BoneChainItem child)
        //{
        //    if (parent.Constraints is null)
        //        return;
            
        //    var parentTfm = parent.Transform;
        //    parentTfm!.Rotation = parentTfm!.BindState.Rotation;
        //    parentTfm!.RecalculateMatrices();
        //    parentTfm!.RecalculateInverseMatrices();
        //    var localPos = parentTfm!.InverseTransformPoint(child.WorldPosSolve);
        //    var constrainedLocalPos = parent.Constraints.ConstrainChildLocalPosition(localPos);
        //    var worldPos = parentTfm.TransformPoint(constrainedLocalPos);
        //    child.WorldPosSolve = worldPos;
        //    parent.WorldChildDirSolve = (child.WorldPosSolve - parent.WorldPosSolve).Normalized();
        //}

        /// <summary>
        /// Initializes the chain by setting the world positions of each bone and calculating the total length.
        /// </summary>
        /// <param name="chain"></param>
        /// <returns></returns>
        private static float PreSolve(BoneChainItem[] chain)
        {
            //Set a copy of the world translation for each bone in the chain
            for (int i = 0; i < chain.Length; i++)
            {
                var tfm = chain[i].Transform;
                if (tfm is not null)
                    chain[i].WorldPosSolve = tfm.WorldTranslation;
                else
                    Debug.LogWarning($"Bone {i} has no transform.");
            }

            //For each bone, set the distance to the next bone and calculate the total length
            //Each bone needs direction and distance to its child set.
            float totalLength = 0.0f;
            chain[^1].DistanceToChild = 0; // Last bone has no length
            for (int i = 0; i < chain.Length - 1; i++)
            {
                BoneChainItem parent = chain[i];
                BoneChainItem child = chain[i + 1];
                parent.SetWorldChildDirTo(child.WorldPosSolve);

                float len = parent.Transform!.BindMatrix.Translation.Distance(child.Transform!.BindMatrix.Translation);
                parent.DistanceToChild = len;
                totalLength += len;
            }
            return totalLength;
        }

        /// <summary>
        /// Applies the calculated world positions and child vectors to the bone transforms.
        /// Call this after iterating through the chain to update world positions.
        /// </summary>
        /// <param name="chain"></param>
        private static void PostSolve(BoneChainItem[] chain)
        {
            var root = chain[0];
            var rootTfm = root.Transform;
            if (rootTfm is null)
                return;
            
            //rootTfm!.Parent?.RecalculateMatrices();
            //rootTfm!.Parent?.RecalculateInverseMatrices();
            rootTfm.SetWorldTranslation(root.WorldPosSolve);

            for (int i = 1; i < chain.Length; i++)
            {
                BoneChainItem parent = chain[i - 1];
                BoneChainItem child = chain[i];

                Transform? pTfm = parent.Transform;
                if (pTfm is null)
                {
                    Debug.LogWarning($"Parent bone has no transform, canceling solve.");
                    break;
                }
                Transform? cTfm = child.Transform;
                if (cTfm is null)
                {
                    Debug.LogWarning($"Child bone has no transform, canceling solve.");
                    break;
                }
                //Set the child's world translation
                bool lastBone = i == chain.Length - 1;
                if (lastBone)
                {
                    //Verify world pos solve is the correct distance from the parent
                    float dist = parent.DistanceToChild;
                    Vector3 parentToChild = parent.WorldChildDirSolve * dist;
                    child.WorldPosSolve = parent.WorldPosSolve + parentToChild;
                }
                //Take current local translation from parent
                //Transform it to parent's world space
                //Get the direction from parent to child
                //Get the delta rotation between the two world-space vectors
                //Add the delta rotation to the parent's current world rotation
                //pTfm.Parent?.RecalculateMatrices();
                //pTfm.Parent?.RecalculateInverseMatrices();
                //pTfm.RecalculateMatrices();
                //pTfm.RecalculateInverseMatrices();
                pTfm.AddWorldRotationDelta(XRMath.RotationBetweenVectors(pTfm.TransformDirection(cTfm.Translation), child.WorldPosSolve - parent.WorldPosSolve));
            }
        }

        //public static Vector3 ConstrainPosition(BoneChainItem bone, Vector3 originalPosition)
        //    => ApplyDistanceCurve(originalPosition, bone.WorldPosSolve, 1.0f, AnimationCurve.EaseOut);

        //public static Quaternion ApplyJointConstraints(BoneChainItem bone, Quaternion desiredRotation)
        //{
        //    //Rotator euler = Rotator.FromQuaternion(desiredRotation);
        //    //euler.NormalizeRotations180();

        //    //var max = bone.Def.MaxRotation;
        //    //var min = bone.Def.MinRotation;

        //    //// Clamp each axis based on bone's rotation limits
        //    //if (min is not null && max is not null)
        //    //{
        //    //    euler.Yaw = euler.Yaw.Clamp(min.Value.Yaw, max.Value.Yaw);
        //    //    euler.Pitch = euler.Pitch.Clamp(min.Value.Pitch, max.Value.Pitch);
        //    //    euler.Roll = euler.Roll.Clamp(min.Value.Roll, max.Value.Roll);
        //    //}
        //    //else if (min is not null)
        //    //{
        //    //    euler.Yaw = euler.Yaw.ClampMin(min.Value.Yaw);
        //    //    euler.Pitch = euler.Pitch.ClampMin(min.Value.Pitch);
        //    //    euler.Roll = euler.Roll.ClampMin(min.Value.Roll);
        //    //}
        //    //else if (max is not null)
        //    //{
        //    //    euler.Yaw = euler.Yaw.ClampMax(max.Value.Yaw);
        //    //    euler.Pitch = euler.Pitch.ClampMax(max.Value.Pitch);
        //    //    euler.Roll = euler.Roll.ClampMax(max.Value.Roll);
        //    //}
        //    return desiredRotation;
        //}
        //public static Vector3 ApplyDistanceCurve(Vector3 originalPosition, Vector3 currentPosition, float maxDistance, AnimationCurve curve)
        //    => originalPosition + (currentPosition - originalPosition) * curve.Evaluate(Vector3.Distance(originalPosition, currentPosition) / maxDistance);
    }
}
