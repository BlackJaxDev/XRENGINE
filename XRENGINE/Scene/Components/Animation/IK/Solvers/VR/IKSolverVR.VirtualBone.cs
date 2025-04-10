using Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Components.Animation
{
    public partial class IKSolverVR
    {
        [System.Serializable]
        public class VirtualBone
        {
            public Vector3 _readPosition;
            public Quaternion _readRotation;

            public Vector3 _solverPosition;
            public Quaternion _solverRotation;

            public float _length;
            public float _lengthSquared;
            public Vector3 _axis;

            public VirtualBone(Vector3 position, Quaternion rotation)
                => Read(position, rotation);

            public void Read(Vector3 position, Quaternion rotation)
            {
                _readPosition = position;
                _readRotation = rotation;
                _solverPosition = position;
                _solverRotation = rotation;
            }

            public static void SwingRotation(VirtualBone[] bones, int index, Vector3 swingTarget, float weight = 1f)
            {
                if (weight <= 0f)
                    return;

                Quaternion r = XRMath.RotationBetweenVectors(
                    bones[index]._solverRotation.Rotate(bones[index]._axis), 
                    swingTarget - bones[index]._solverPosition);

                if (weight < 1f)
                    r = Quaternion.Lerp(Quaternion.Identity, r, weight);

                for (int i = index; i < bones.Length; i++)
                    bones[i]._solverRotation = r * bones[i]._solverRotation;
            }

            // Calculates bone lengths and axes, returns the length of the entire chain
            public static float PreSolve(ref VirtualBone[] bones)
            {
                float length = 0;

                for (int i = 0; i < bones.Length; i++)
                {
                    if (i < bones.Length - 1)
                    {
                        bones[i]._lengthSquared = (bones[i + 1]._solverPosition - bones[i]._solverPosition).LengthSquared();
                        bones[i]._length = MathF.Sqrt(bones[i]._lengthSquared);
                        length += bones[i]._length;

                        bones[i]._axis = Quaternion.Inverse(bones[i]._solverRotation).Rotate(bones[i + 1]._solverPosition - bones[i]._solverPosition);
                    }
                    else
                    {
                        bones[i]._lengthSquared = 0f;
                        bones[i]._length = 0f;
                    }
                }

                return length;
            }

            public static void RotateAroundPoint(VirtualBone[] bones, int index, Vector3 point, Quaternion rotation)
            {
                for (int i = index; i < bones.Length; i++)
                {
                    if (bones[i] is null)
                        continue;
                    
                    Vector3 dir = bones[i]._solverPosition - point;
                    bones[i]._solverPosition = point + rotation.Rotate(dir);
                    bones[i]._solverRotation = rotation * bones[i]._solverRotation;
                }
            }

            public static void RotateBy(VirtualBone[] bones, int index, Quaternion rotation)
            {
                for (int i = index; i < bones.Length; i++)
                {
                    if (bones[i] is null)
                        continue;
                    
                    Vector3 dir = bones[i]._solverPosition - bones[index]._solverPosition;
                    bones[i]._solverPosition = bones[index]._solverPosition + rotation.Rotate(dir);
                    bones[i]._solverRotation = rotation * bones[i]._solverRotation;
                }
            }

            public static void RotateBy(VirtualBone[] bones, Quaternion rotation)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] is null)
                        continue;
                    
                    if (i > 0)
                        bones[i]._solverPosition = bones[0]._solverPosition + rotation.Rotate(bones[i]._solverPosition - bones[0]._solverPosition);
                    
                    bones[i]._solverRotation = rotation * bones[i]._solverRotation;
                }
            }

            public static void RotateTo(VirtualBone[] bones, int index, Quaternion rotation)
            {
                Quaternion q = XRMath.FromToRotation(bones[index]._solverRotation, rotation);
                RotateAroundPoint(bones, index, bones[index]._solverPosition, q);
            }

            // TODO Move to IKSolverTrigonometric
            /// <summary>
            /// Solve the bone chain virtually using both solverPositions and SolverRotations. This will work the same as IKSolverTrigonometric.Solve.
            /// </summary>
            public static void SolveTrigonometric(VirtualBone[] bones, int first, int second, int third, Vector3 targetPosition, Vector3 bendNormal, float weight)
            {
                if (weight <= 0f)
                    return;

                // Direction of the limb in solver
                targetPosition = Vector3.Lerp(bones[third]._solverPosition, targetPosition, weight);

                Vector3 dir = targetPosition - bones[first]._solverPosition;

                // Distance between the first and the last transform solver positions
                float sqrMag = dir.LengthSquared();
                if (sqrMag == 0f)
                    return;
                float length = MathF.Sqrt(sqrMag);

                float sqrMag1 = (bones[second]._solverPosition - bones[first]._solverPosition).LengthSquared();
                float sqrMag2 = (bones[third]._solverPosition - bones[second]._solverPosition).LengthSquared();

                // Get the general world space bending direction
                Vector3 bendDir = Vector3.Cross(dir, bendNormal);

                // Get the direction to the trigonometrically solved position of the second transform
                Vector3 toBendPoint = GetDirectionToBendPoint(dir, length, bendDir, sqrMag1, sqrMag2);

                // Position the second transform
                Quaternion q1 = XRMath.RotationBetweenVectors(
                    bones[second]._solverPosition - bones[first]._solverPosition,
                    toBendPoint);

                if (weight < 1f)
                    q1 = Quaternion.Lerp(Quaternion.Identity, q1, weight);

                RotateAroundPoint(bones, first, bones[first]._solverPosition, q1);

                Quaternion q2 = XRMath.RotationBetweenVectors(
                    bones[third]._solverPosition - bones[second]._solverPosition,
                    targetPosition - bones[second]._solverPosition);

                if (weight < 1f)
                    q2 = Quaternion.Lerp(Quaternion.Identity, q2, weight);

                RotateAroundPoint(bones, second, bones[second]._solverPosition, q2);
            }

            //Calculates the bend direction based on the law of cosines. NB! Magnitude of the returned vector does not equal to the length of the first bone!
            private static Vector3 GetDirectionToBendPoint(Vector3 direction, float directionMag, Vector3 bendDirection, float sqrMag1, float sqrMag2)
            {
                if (direction == Vector3.Zero)
                    return Vector3.Zero;

                float x = ((directionMag * directionMag) + (sqrMag1 - sqrMag2)) / 2f / directionMag;
                return XRMath.LookRotation(direction, bendDirection).Rotate(new Vector3(
                        0f,
                        (float)Math.Sqrt((sqrMag1 - x * x).Clamp(0f, float.PositiveInfinity)),
                        x));
            }

            // TODO Move to IKSolverFABRIK
            // Solves a simple FABRIK pass for a bone hierarchy, not using rotation limits or singularity breaking here
            public static void SolveFABRIK(VirtualBone[] bones, Vector3 startPosition, Vector3 targetPosition, float weight, float minNormalizedTargetDistance, int iterations, float length, Vector3 startOffset)
            {
                if (weight <= 0f)
                    return;

                if (minNormalizedTargetDistance > 0f)
                {
                    Vector3 targetDirection = targetPosition - startPosition;
                    float targetLength = targetDirection.Length();
                    Vector3 tP = startPosition + (targetDirection / targetLength) * MathF.Max(length * minNormalizedTargetDistance, targetLength);
                    targetPosition = Vector3.Lerp(targetPosition, tP, weight);
                }

                // Iterating the solver
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    // Stage 1
                    bones[^1]._solverPosition = Vector3.Lerp(bones[^1]._solverPosition, targetPosition, weight);

                    // Finding joint positions
                    for (int i = bones.Length - 2; i > -1; i--)
                        bones[i]._solverPosition = SolveFABRIKJoint(bones[i]._solverPosition, bones[i + 1]._solverPosition, bones[i]._length);
                    
                    // Stage 2
                    if (iteration == 0)
                        foreach (VirtualBone bone in bones)
                            bone._solverPosition += startOffset;
                    
                    bones[0]._solverPosition = startPosition;

                    for (int i = 1; i < bones.Length; i++)
                        bones[i]._solverPosition = SolveFABRIKJoint(bones[i]._solverPosition, bones[i - 1]._solverPosition, bones[i - 1]._length);
                }

                for (int i = 0; i < bones.Length - 1; i++)
                    SwingRotation(bones, i, bones[i + 1]._solverPosition);
            }

            // Solves a FABRIK joint between two bones.
            private static Vector3 SolveFABRIKJoint(Vector3 pos1, Vector3 pos2, float length)
                => pos2 + (pos1 - pos2).Normalized() * length;

            public static void SolveCCD(VirtualBone[] bones, Vector3 targetPosition, float weight, int iterations)
            {
                if (weight <= 0f)
                    return;

                // Iterating the solver
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    for (int i = bones.Length - 2; i > -1; i--)
                    {
                        Vector3 toLastBone = bones[^1]._solverPosition - bones[i]._solverPosition;
                        Vector3 toTarget = targetPosition - bones[i]._solverPosition;
                        Quaternion rotation = XRMath.RotationBetweenVectors(toLastBone, toTarget);
                        RotateBy(bones, i, weight >= 1 ? rotation : Quaternion.Lerp(Quaternion.Identity, rotation, weight));
                    }
                }
            }
        }
    }
}
