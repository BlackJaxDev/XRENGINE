using Extensions;
using System.Numerics;
using XREngine.Data.Core;
using static XREngine.Components.Animation.IKSolverVR.SolverTransforms;

namespace XREngine.Components.Animation
{
    public partial class IKSolverVR
    {
        [Serializable]
        public class VirtualBone(TransformPoses pose) : XRBase
        {
            public TransformPoses Pose { get; } = pose ?? throw new ArgumentNullException(nameof(pose), "VirtualBone Pose cannot be null.");
            public Quaternion SolverRotation
            { 
                get => Pose.SolvedWorld.Rotation;
                set => Pose.SolvedWorld.Rotation = value;
            }
            public Vector3 SolverPosition
            {
                get => Pose.SolvedWorld.Translation;
                set => Pose.SolvedWorld.Translation = value;
            }
            public Vector3 DefaultPosition
            {
                get => Pose.DefaultLocal.Translation;
                set => Pose.DefaultLocal.Translation = value;
            }
            public Quaternion DefaultRotation
            {
                get => Pose.DefaultLocal.Rotation;
                set => Pose.DefaultLocal.Rotation = value;
            }
            public Vector3 InputPosition
            {
                get => Pose.InputWorld.Translation;
                set => Pose.InputWorld.Translation = value;
            }
            public Quaternion InputRotation
            {
                get => Pose.InputWorld.Rotation;
                set => Pose.InputWorld.Rotation = value;
            }

            private float _length = 0.0f;
            public float Length
            {
                get => _length;
                set => SetField(ref _length, value);
            }

            private Vector3 _axis;
            public Vector3 Axis
            {
                get => _axis;
                set => SetField(ref _axis, value);
            }

            private float _lengthSquared = 0.0f;
            public float LengthSquared
            {
                get => _lengthSquared;
                set => SetField(ref _lengthSquared, value);
            }

            /// <summary>
            /// Applies a swing rotation to the bone chain starting from the specified index.
            /// </summary>
            /// <param name="bones"></param>
            /// <param name="index"></param>
            /// <param name="swingTarget"></param>
            /// <param name="weight"></param>
            public static void SwingRotation(
                VirtualBone[] bones,
                int index,
                Vector3 swingTarget,
                float weight = 1.0f)
            {
                if (weight <= 0.0f)
                    return;

                var bone = bones[index];
                var initialVector = bone.SolverRotation.Rotate(bone.Axis);
                var targetVector = swingTarget - bone.SolverPosition;

                Quaternion r = XRMath.RotationBetweenVectors(initialVector, targetVector);
                if (weight < 1.0f)
                    r = Quaternion.Lerp(Quaternion.Identity, r, weight);

                for (int i = index; i < bones.Length; i++)
                {
                    var b = bones[i];
                    b.SolverRotation = r * b.SolverRotation;
                }
            }

            /// <summary>
            /// Calculates bone lengths and axes, returns the length of the entire chain
            /// </summary>
            /// <param name="bones"></param>
            /// <returns></returns>
            public static float PreSolve(ref VirtualBone[] bones)
            {
                float length = 0;
                for (int i = 0; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    if (i == bones.Length - 1)
                        bone.Length = 0.0f;
                    else
                    {
                        Vector3 diff = bones[i + 1].SolverPosition - bone.SolverPosition;

                        float ls = diff.LengthSquared();
                        bone.LengthSquared = ls;

                        float l = MathF.Sqrt(ls);
                        bone.Length = l;

                        bone.Axis = Quaternion.Inverse(bone.SolverRotation).Rotate(diff);

                        length += l;
                    }
                }
                return length;
            }

            public static void RotateAroundPoint(
                VirtualBone[] bones,
                int startIndex,
                Vector3 point,
                Quaternion rotation)
            {
                //if (rotation == Quaternion.Identity)
                //    return;

                for (int i = startIndex; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    if (bone is null)
                        continue;
                    
                    Vector3 dir = bone.SolverPosition - point;
                    bone.SolverPosition = point + rotation.Rotate(dir);
                    bone.SolverRotation = rotation * bone.SolverRotation;
                }
            }

            public static void RotateBy(VirtualBone[] bones, int index, Quaternion rotation)
            {
                for (int i = index; i < bones.Length; i++)
                {
                    if (bones[i] is null)
                        continue;
                    
                    Vector3 dir = bones[i].SolverPosition - bones[index].SolverPosition;
                    bones[i].SolverPosition = bones[index].SolverPosition + rotation.Rotate(dir);
                    bones[i].SolverRotation = rotation * bones[i].SolverRotation;
                }
            }

            public static void RotateBy(VirtualBone[] bones, Quaternion rotation)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] is null)
                        continue;

                    if (i > 0)
                    {
                        Vector3 dir = bones[i].SolverPosition - bones[0].SolverPosition;
                        bones[i].SolverPosition = bones[0].SolverPosition + rotation.Rotate(dir);
                    }
                    
                    bones[i].SolverRotation = rotation * bones[i].SolverRotation;
                }
            }

            public static void RotateTo(VirtualBone[] bones, int index, Quaternion rotation)
            {
                Quaternion q = XRMath.FromToRotation(bones[index].SolverRotation, rotation);
                RotateAroundPoint(bones, index, bones[index].SolverPosition, q);
            }

            public static void SolveTrigonometric(VirtualBone[] bones, int first, int second, int third, Vector3 targetPosition, Vector3 bendNormal, float weight)
            {
                if (weight <= 0.0f)
                    return;

                // Direction of the limb in solver
                targetPosition = Vector3.Lerp(bones[third].SolverPosition, targetPosition, weight);

                Vector3 bendDir = GetBendDirection(
                    bones,
                    first,
                    second,
                    targetPosition,
                    bendNormal);

                Vector3 firstToSecond = bones[second].SolverPosition - bones[first].SolverPosition;
                Quaternion q1 = XRMath.RotationBetweenVectors(firstToSecond, bendDir).Normalized();

                if (weight < 1.0f)
                    q1 = Quaternion.Lerp(Quaternion.Identity, q1, weight);

                RotateAroundPoint(bones, first, bones[first].SolverPosition, q1);

                Vector3 secondToThird = bones[third].SolverPosition - bones[second].SolverPosition;
                Vector3 secondToTarget = targetPosition - bones[second].SolverPosition;
                Quaternion q2 = XRMath.RotationBetweenVectors(secondToThird, secondToTarget).Normalized();

                if (weight < 1.0f)
                    q2 = Quaternion.Lerp(Quaternion.Identity, q2, weight);

                RotateAroundPoint(bones, second, bones[second].SolverPosition, q2);
            }

            private static Vector3 GetBendDirection(
                VirtualBone[] bones,
                int first,
                int second,
                Vector3 targetPosition,
                Vector3 bendNormal)
            {
                Vector3 dir = targetPosition - bones[first].SolverPosition;
                if (dir == Vector3.Zero)
                    return Vector3.Zero;

                // Distance between the first and the last transform solver positions
                float sqrMag = dir.LengthSquared();
                if (sqrMag < float.Epsilon)
                    return Vector3.Zero;

                float dirMag = MathF.Sqrt(sqrMag);
                float sqrMag1 = bones[first].LengthSquared;
                float sqrMag2 = bones[second].LengthSquared;

                float x = ((dirMag * dirMag) + (sqrMag1 - sqrMag2)) / 2.0f / dirMag;
                float y = (float)Math.Sqrt((sqrMag1 - x * x).ClampMin(0.0f));

                //Vector3 yDirection = Vector3.Cross(bendNormal, dir / dirMag);
                Vector3 lookRot = Vector3.Cross(bendNormal, dir);
                return XRMath.LookRotation(dir, lookRot).Rotate(new Vector3(0.0f, y, -x));
            }

            // TODO Move to IKSolverFABRIK
            // Solves a simple FABRIK pass for a bone hierarchy, not using rotation limits or singularity breaking here
            public static void SolveFABRIK(
                VirtualBone[] bones,
                Vector3 startPosition,
                Vector3 targetPosition,
                float weight,
                float minNormalizedTargetDistance,
                int iterations,
                float length,
                Vector3 startOffset)
            {
                if (weight <= 0.0f)
                    return;

                if (minNormalizedTargetDistance > 0.0f)
                {
                    Vector3 targetDirection = targetPosition - startPosition;
                    float targetLength = targetDirection.Length();
                    Vector3 tP = startPosition + (targetDirection / targetLength) * MathF.Max(length * minNormalizedTargetDistance, targetLength);
                    targetPosition = Vector3.Lerp(targetPosition, tP, weight);
                }

                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    //Stage 1: Backward pass

                    //Set the last bone to the target position
                    bones[^1].SolverPosition = Vector3.Lerp(bones[^1].SolverPosition, targetPosition, weight);
                    for (int i = bones.Length - 2; i >= 0; i--)
                    {
                        var currBone = bones[i];
                        var prevBone = bones[i + 1];

                        currBone.SolverPosition = SolveFABRIKJoint(
                            currBone.SolverPosition,
                            prevBone.SolverPosition,
                            currBone.Length);
                    }

                    //Stage 2: Forward pass

                    //If this is the first iteration, apply the start offset to all bones
                    if (iteration == 0)
                        foreach (VirtualBone bone in bones)
                            bone.SolverPosition += startOffset;

                    //Set the first bone to the start position
                    bones[0].SolverPosition = startPosition;
                    for (int i = 1; i < bones.Length; i++)
                    {
                        var currBone = bones[i];
                        var prevBone = bones[i - 1];

                        currBone.SolverPosition = SolveFABRIKJoint(
                            currBone.SolverPosition,
                            prevBone.SolverPosition,
                            prevBone.Length);
                    }
                }

                for (int i = 0; i < bones.Length - 1; i++)
                    SwingRotation(bones, i, bones[i + 1].SolverPosition);
            }

            // Solves a FABRIK joint between two bones.
            private static Vector3 SolveFABRIKJoint(Vector3 end, Vector3 start, float length)
                => start + (end - start).Normalized() * length;

            public static void SolveCCD(VirtualBone[] bones, Vector3 targetPosition, float weight, int iterations)
            {
                if (weight <= 0.0f)
                    return;

                // Iterating the solver
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    for (int i = bones.Length - 2; i > -1; i--)
                    {
                        Vector3 toLastBone = bones[^1].SolverPosition - bones[i].SolverPosition;
                        Vector3 toTarget = targetPosition - bones[i].SolverPosition;
                        Quaternion rotation = XRMath.RotationBetweenVectors(toLastBone, toTarget);
                        RotateBy(bones, i, weight >= 1.0f ? rotation : Quaternion.Lerp(Quaternion.Identity, rotation, weight));
                    }
                }
            }
        }
    }
}
