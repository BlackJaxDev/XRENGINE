using Extensions;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    public partial class IKSolverVR
    {
        /// <summary>
        /// 4-segmented analytic arm chain.
        /// </summary>
        [System.Serializable]
        public class Arm : BodyPart
        {
            [System.Serializable]
            public enum ShoulderRotationMode
            {
                YawPitch,
                FromTo
            }

            /// <summary>
            /// The hand target. This should not be the hand controller itself, but a child GameObject parented to it so you could adjust its position/rotation to match the orientation of the hand bone. The best practice for setup would be to move the hand controller to the avatar's hand as it it was held by the avatar, duplicate the avatar's hand bone and parent it to the hand controller. Then assign the duplicate to this slot.
            /// </summary>
            public Transform? _target;

            /// <summary>
            /// Positional weight of the hand target. Note that if you have nulled the target, the hand will still be pulled to the last position of the target until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _positionWeight = 1f;

            /// <summary>
            /// Rotational weight of the hand target. Note that if you have nulled the target, the hand will still be rotated to the last rotation of the target until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _rotationWeight = 1f;

            /// <summary>
            /// The weight of shoulder rotation.
            /// </summary>
            [Range(0f, 1f)]
            public float _shoulderRotationWeight = 1f;

            /// <summary>
            /// Different techniques for shoulder bone rotation.
            /// </summary>
            public ShoulderRotationMode _shoulderRotationMode = ShoulderRotationMode.YawPitch;

            /// <summary>
            /// The weight of twisting the shoulders backwards when arms are lifted up.
            /// </summary>
            public float _shoulderTwistWeight = 1f;

            /// <summary>
            /// Tweak this value to adjust shoulder rotation around the yaw (up) axis.
            /// </summary>
            public float _shoulderYawOffset = 45f;

            /// <summary>
            /// Tweak this value to adjust shoulder rotation around the pitch (forward) axis.
            /// </summary>
            public float _shoulderPitchOffset = -30f;

            /// <summary>
            /// The elbow will be bent towards this Transform if 'Bend Goal Weight' > 0.
            /// </summary>
            public Transform? _bendGoal;

            /// <summary>
            /// If greater than 0, will bend the elbow towards the 'Bend Goal' Transform.
            /// </summary>
            [Range(0f, 1f)]
            public float _bendGoalWeight;

            /// <summary>
            /// Angular offset of the elbow bending direction.
            /// </summary>
            [Range(-180f, 180f)]
            public float _swivelOffset;

            /// <summary>
            /// Local axis of the hand bone that points from the wrist towards the palm. Used for defining hand bone orientation. If you have copied VRIK component from another avatar that has different bone orientations, right-click on VRIK header and select 'Guess Hand Orientations' from the context menu.
            /// </summary>
            public Vector3 _wristToPalmAxis = Vector3.Zero;

            /// <summary>
            /// Local axis of the hand bone that points from the palm towards the thumb. Used for defining hand bone orientation If you have copied VRIK component from another avatar that has different bone orientations, right-click on VRIK header and select 'Guess Hand Orientations' from the context menu..
            /// </summary>
            public Vector3 _palmToThumbAxis = Vector3.Zero;

            /// <summary>
            /// Use this to make the arm shorter/longer. Works by displacement of hand and forearm localPosition.
            /// </summary>
            [Range(0.01f, 2f)]
            public float _armLengthMlp = 1f;

            /// <summary>
            /// 'Time' represents (target distance / arm length) and 'value' represents the amount of stretching. So value at time 1 represents stretching amount at the point where distance to the target is equal to arm length. Value at time 2 represents stretching amount at the point where distance to the target is double the arm length. Linear stretching would be achieved with a linear curve going up by 45 degrees. Increase the range of stretching by moving the last key up and right by the same amount. Smoothing in the curve can help reduce elbow snapping (start stretching the arm slightly before target distance reaches arm length). To get a good optimal value for this curve, please go to the 'VRIK (Basic)' demo scene and copy the stretch curve over from the Pilot character.
            /// </summary>
            public AnimationCurve _stretchCurve = new();

            /// <summary>
            /// Target position of the hand. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 IKPosition;

            /// <summary>
            /// Target rotation of the hand. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion IKRotation = Quaternion.Identity;

            /// <summary>
            /// The bending direction of the limb. Will be used if bendGoalWeight is greater than 0. Will be overwritten if bendGoal is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _bendDirection = Globals.Backward;

            /// <summary>
            /// Position offset of the hand. Will be applied on top of hand target position and reset to Vector3.zero after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _handPositionOffset;

            // Gets the target position of the hand.
            public Vector3 Position { get; private set; }

            // Gets the target rotation of the hand
            public Quaternion Rotation { get; private set; }

            private bool _hasShoulder;

            private VirtualBone Shoulder => _bones[0];
            private VirtualBone UpperArm => _bones[_hasShoulder ? 1 : 0];
            private VirtualBone Forearm => _bones[_hasShoulder ? 2 : 1];
            private VirtualBone Hand => _bones[_hasShoulder ? 3 : 2];

            private Vector3 chestForwardAxis;
            private Vector3 chestUpAxis;
            private Quaternion _chestRotation = Quaternion.Identity;
            private Vector3 _chestForward;
            private Vector3 _chestUp;
            private Quaternion forearmRelToUpperArm = Quaternion.Identity;
            private Vector3 _upperArmBendAxis;

            protected override void OnRead(Vector3[] positions, Quaternion[] rotations, bool hasChest, bool hasNeck, bool hasShoulders, bool hasToes, bool hasLegs, int rootIndex, int index)
            {
                Vector3 shoulderPosition = positions[index];
                Quaternion shoulderRotation = rotations[index];
                Vector3 upperArmPosition = positions[index + 1];
                Quaternion upperArmRotation = rotations[index + 1];
                Vector3 forearmPosition = positions[index + 2];
                Quaternion forearmRotation = rotations[index + 2];
                Vector3 handPosition = positions[index + 3];
                Quaternion handRotation = rotations[index + 3];

                if (!_initialized)
                {
                    IKPosition = handPosition;
                    IKRotation = handRotation;
                    Rotation = IKRotation;

                    this._hasShoulder = hasShoulders;

                    _bones = new VirtualBone[_hasShoulder ? 4 : 3];

                    if (_hasShoulder)
                    {
                        _bones[0] = new VirtualBone(shoulderPosition, shoulderRotation);
                        _bones[1] = new VirtualBone(upperArmPosition, upperArmRotation);
                        _bones[2] = new VirtualBone(forearmPosition, forearmRotation);
                        _bones[3] = new VirtualBone(handPosition, handRotation);
                    }
                    else
                    {
                        _bones[0] = new VirtualBone(upperArmPosition, upperArmRotation);
                        _bones[1] = new VirtualBone(forearmPosition, forearmRotation);
                        _bones[2] = new VirtualBone(handPosition, handRotation);
                    }

                    Vector3 rootForward = rotations[0].Rotate(Globals.Forward);
                    chestForwardAxis = Quaternion.Inverse(_rootRotation).Rotate(rootForward);
                    chestUpAxis = Quaternion.Inverse(_rootRotation).Rotate(rotations[0].Rotate(Globals.Up));

                    // Get the local axis of the upper arm pointing towards the bend normal
                    Vector3 upperArmForwardAxis = XRMath.GetAxisVectorToDirection(upperArmRotation, rootForward);
                    if (Vector3.Dot(upperArmRotation.Rotate(upperArmForwardAxis), rootForward) < 0f)
                        upperArmForwardAxis = -upperArmForwardAxis;
                    _upperArmBendAxis = Vector3.Cross(Quaternion.Inverse(upperArmRotation).Rotate(forearmPosition - upperArmPosition), upperArmForwardAxis);
                    if (_upperArmBendAxis == Vector3.Zero)
                        Debug.LogWarning("VRIK can not calculate which way to bend the arms because the arms are perfectly straight. Please rotate the elbow bones slightly in their natural bending direction in the Editor.");
                }

                if (_hasShoulder)
                {
                    _bones[0].Read(shoulderPosition, shoulderRotation);
                    _bones[1].Read(upperArmPosition, upperArmRotation);
                    _bones[2].Read(forearmPosition, forearmRotation);
                    _bones[3].Read(handPosition, handRotation);
                }
                else
                {
                    _bones[0].Read(upperArmPosition, upperArmRotation);
                    _bones[1].Read(forearmPosition, forearmRotation);
                    _bones[2].Read(handPosition, handRotation);
                }
            }

            public override void PreSolve(float scale)
            {
                if (_target != null)
                {
                    IKPosition = _target.WorldTranslation;
                    IKRotation = _target.WorldRotation;
                }

                Position = XRMath.Lerp(Hand._solverPosition, IKPosition, _positionWeight);
                Rotation = XRMath.Lerp(Hand._solverRotation, IKRotation, _rotationWeight);

                Shoulder._axis = Shoulder._axis.Normalized();
                forearmRelToUpperArm = Quaternion.Inverse(UpperArm._solverRotation) * Forearm._solverRotation;
            }

            public override void ApplyOffsets(float scale)
                => Position += _handPositionOffset;

            private void Stretching()
            {
                // Adjusting arm length
                float armLength = UpperArm._length + Forearm._length;
                Vector3 elbowAdd = Vector3.Zero;
                Vector3 handAdd = Vector3.Zero;

                if (_armLengthMlp != 1f)
                {
                    armLength *= _armLengthMlp;
                    elbowAdd = (Forearm._solverPosition - UpperArm._solverPosition) * (_armLengthMlp - 1f);
                    handAdd = (Hand._solverPosition - Forearm._solverPosition) * (_armLengthMlp - 1f);
                    Forearm._solverPosition += elbowAdd;
                    Hand._solverPosition += elbowAdd + handAdd;
                }

                // Stretching
                float distanceToTarget = Vector3.Distance(UpperArm._solverPosition, Position);
                float stretchF = distanceToTarget / armLength;

                float m = _stretchCurve.Evaluate(stretchF);
                m *= _positionWeight;

                elbowAdd = (Forearm._solverPosition - UpperArm._solverPosition) * m;
                handAdd = (Hand._solverPosition - Forearm._solverPosition) * m;

                Forearm._solverPosition += elbowAdd;
                Hand._solverPosition += elbowAdd + handAdd;
            }

            public void Solve(bool isLeft)
            {
                _chestRotation = XRMath.LookRotation(_rootRotation.Rotate(chestForwardAxis), _rootRotation.Rotate(chestUpAxis));
                _chestForward = _chestRotation.Rotate(Globals.Forward);
                _chestUp = _chestRotation.Rotate(Globals.Up);

                //Debug.DrawRay (Vector3.up * 2f, chestForward);
                //Debug.DrawRay (Vector3.up * 2f, chestUp);

                Vector3 bendNormal = Vector3.Zero;

                if (_hasShoulder && _shoulderRotationWeight > 0f && LOD < 1)
                {
                    switch (_shoulderRotationMode)
                    {
                        case ShoulderRotationMode.YawPitch:
                            {
                                Vector3 sDir = Position - Shoulder._solverPosition;
                                sDir = sDir.Normalized();

                                // Shoulder Yaw
                                float yOA = isLeft ? _shoulderYawOffset : -_shoulderYawOffset;
                                Quaternion yawOffset = Quaternion.CreateFromAxisAngle(_chestUp, float.DegreesToRadians((isLeft ? -90f : 90f) + yOA));
                                Quaternion workingSpace = yawOffset * _chestRotation;

                                //Debug.DrawRay(Vector3.up * 2f, workingSpace * Vector3.forward);
                                //Debug.DrawRay(Vector3.up * 2f, workingSpace * Vector3.up);

                                Vector3 sDirWorking = Quaternion.Inverse(workingSpace).Rotate(sDir);

                                //Debug.DrawRay(Vector3.up * 2f, sDirWorking);

                                float yaw = float.RadiansToDegrees(MathF.Atan2(sDirWorking.X, sDirWorking.Z));

                                float dotY = Vector3.Dot(sDirWorking, Globals.Up);
                                dotY = 1f - MathF.Abs(dotY);
                                yaw *= dotY;

                                yaw -= yOA;
                                float yawLimitMin = isLeft ? -20f : -50f;
                                float yawLimitMax = isLeft ? 50f : 20f;
                                yaw = DamperValue(yaw, yawLimitMin - yOA, yawLimitMax - yOA, 0.7f); // back, forward

                                Vector3 f = Shoulder._solverRotation.Rotate(Shoulder._axis);
                                Vector3 t = workingSpace.Rotate((Quaternion.CreateFromAxisAngle(Globals.Up, float.DegreesToRadians(yaw)).Rotate(Globals.Forward)));
                                Quaternion yawRotation = XRMath.RotationBetweenVectors(f, t);

                                //Debug.DrawRay(Vector3.up * 2f, f, Color.red);
                                //Debug.DrawRay(Vector3.up * 2f, t, Color.green);

                                //Debug.DrawRay(Vector3.up * 2f, yawRotation * Vector3.forward, Color.blue);
                                //Debug.DrawRay(Vector3.up * 2f, yawRotation * Vector3.up, Color.green);
                                //Debug.DrawRay(Vector3.up * 2f, yawRotation * Vector3.right, Color.red);

                                // Shoulder Pitch
                                Quaternion pitchOffset = Quaternion.CreateFromAxisAngle(_chestUp, float.DegreesToRadians(isLeft ? -90f : 90f));
                                workingSpace = pitchOffset * _chestRotation;
                                workingSpace = Quaternion.CreateFromAxisAngle(_chestForward, isLeft ? _shoulderPitchOffset : -_shoulderPitchOffset) * workingSpace;

                                //Debug.DrawRay(Vector3.up * 2f, workingSpace * Vector3.forward);
                                //Debug.DrawRay(Vector3.up * 2f, workingSpace * Vector3.up);

                                sDir = Position - (Shoulder._solverPosition + _chestRotation.Rotate(isLeft ? Globals.Right : Globals.Left) * Length);
                                sDirWorking = Quaternion.Inverse(workingSpace).Rotate(sDir);

                                //Debug.DrawRay(Vector3.up * 2f, sDirWorking);

                                float pitchDeg = float.RadiansToDegrees(MathF.Atan2(sDirWorking.Y, sDirWorking.Z));

                                pitchDeg -= _shoulderPitchOffset;
                                pitchDeg = DamperValue(pitchDeg, -45f - _shoulderPitchOffset, 45f - _shoulderPitchOffset);

                                Quaternion pitchRotation = Quaternion.CreateFromAxisAngle(workingSpace.Rotate(Globals.Right), float.DegreesToRadians(-pitchDeg));

                                //Debug.DrawRay(Vector3.up * 2f, pitchRotation * Vector3.forward, Color.green);
                                //Debug.DrawRay(Vector3.up * 2f, pitchRotation * Vector3.up, Color.green);

                                // Rotate bones
                                Quaternion sR = pitchRotation * yawRotation;
                                if (_shoulderRotationWeight * _positionWeight < 1f)
                                    sR = Quaternion.Lerp(Quaternion.Identity, sR, _shoulderRotationWeight * _positionWeight);
                                VirtualBone.RotateBy(_bones, sR);

                                Stretching();

                                // Solve trigonometric
                                bendNormal = GetBendNormal(Position - UpperArm._solverPosition);
                                VirtualBone.SolveTrigonometric(_bones, 1, 2, 3, Position, bendNormal, _positionWeight);

                                float pitchRad = float.DegreesToRadians((pitchDeg * _positionWeight * _shoulderRotationWeight * _shoulderTwistWeight * 2f).Clamp(0f, 180f));
                                Shoulder._solverRotation = Quaternion.CreateFromAxisAngle(Shoulder._solverRotation.Rotate(isLeft ? Shoulder._axis : -Shoulder._axis), pitchRad) * Shoulder._solverRotation;
                                UpperArm._solverRotation = Quaternion.CreateFromAxisAngle(UpperArm._solverRotation.Rotate(isLeft ? UpperArm._axis : -UpperArm._axis), pitchRad) * UpperArm._solverRotation;

                                // Additional pass to reach with the shoulders
                                //VirtualBone.SolveTrigonometric(bones, 0, 1, 3, position, Vector3.Cross(upperArm.solverPosition - shoulder.solverPosition, hand.solverPosition - shoulder.solverPosition), positionWeight * 0.5f);
                                break;
                            }
                        case ShoulderRotationMode.FromTo:
                            {
                                Quaternion shoulderRotation = Shoulder._solverRotation;

                                Quaternion r = XRMath.RotationBetweenVectors(
                                    (UpperArm._solverPosition - Shoulder._solverPosition).Normalized() + _chestForward,
                                    Position - Shoulder._solverPosition);

                                r = Quaternion.Slerp(Quaternion.Identity, r, 0.5f * _shoulderRotationWeight * _positionWeight);
                                VirtualBone.RotateBy(_bones, r);

                                Stretching();

                                VirtualBone.SolveTrigonometric(_bones, 0, 2, 3, Position, Vector3.Cross(Forearm._solverPosition - Shoulder._solverPosition, Hand._solverPosition - Shoulder._solverPosition), 0.5f * _shoulderRotationWeight * _positionWeight);
                                bendNormal = GetBendNormal(Position - UpperArm._solverPosition);
                                VirtualBone.SolveTrigonometric(_bones, 1, 2, 3, Position, bendNormal, _positionWeight);

                                // Twist shoulder and upper arm bones when holding hands up
                                Quaternion q = Quaternion.Inverse(XRMath.LookRotation(_chestUp, _chestForward));
                                Vector3 vBefore = q.Rotate(shoulderRotation.Rotate(Shoulder._axis));
                                Vector3 vAfter = q.Rotate(Shoulder._solverRotation.Rotate(Shoulder._axis));

                                float angleBefore = float.RadiansToDegrees(MathF.Atan2(vBefore.X, vBefore.Z));
                                float angleAfter = float.RadiansToDegrees(MathF.Atan2(vAfter.X, vAfter.Z));
                                float pitchAngle = XRMath.DeltaAngle(angleBefore, angleAfter);

                                if (isLeft) 
                                    pitchAngle = -pitchAngle;

                                pitchAngle = (pitchAngle * _shoulderRotationWeight * _shoulderTwistWeight * 2f * _positionWeight).Clamp(0f, 180f);

                                Shoulder._solverRotation = Quaternion.CreateFromAxisAngle(Shoulder._solverRotation.Rotate(isLeft ? Shoulder._axis : -Shoulder._axis), pitchAngle) * Shoulder._solverRotation;
                                UpperArm._solverRotation = Quaternion.CreateFromAxisAngle(UpperArm._solverRotation.Rotate(isLeft ? UpperArm._axis : -UpperArm._axis), pitchAngle) * UpperArm._solverRotation;
                                break;
                            }
                    }
                }
                else
                {
                    if (LOD < 1)
                        Stretching();

                    bendNormal = GetBendNormal(Position - UpperArm._solverPosition);

                    // Solve arm trigonometric
                    if (_hasShoulder)
                        VirtualBone.SolveTrigonometric(_bones, 1, 2, 3, Position, bendNormal, _positionWeight);
                    else
                        VirtualBone.SolveTrigonometric(_bones, 0, 1, 2, Position, bendNormal, _positionWeight);
                }

                if (LOD < 1 && _positionWeight > 0f)
                {
                    // Fix upperarm twist relative to bend normal
                    Quaternion space = XRMath.LookRotation(UpperArm._solverRotation.Rotate(_upperArmBendAxis), Forearm._solverPosition - UpperArm._solverPosition);
                    Vector3 upperArmTwist = Quaternion.Inverse(space).Rotate(bendNormal);
                    float angleRad = MathF.Atan2(upperArmTwist.X, upperArmTwist.Z);
                    UpperArm._solverRotation = Quaternion.CreateFromAxisAngle(Forearm._solverPosition - UpperArm._solverPosition, angleRad * _positionWeight) * UpperArm._solverRotation;

                    // Fix forearm twist relative to upper arm
                    Quaternion forearmFixed = UpperArm._solverRotation * forearmRelToUpperArm;
                    Quaternion fromTo = XRMath.RotationBetweenVectors(forearmFixed.Rotate(Forearm._axis), Hand._solverPosition - Forearm._solverPosition);
                    RotateTo(Forearm, fromTo * forearmFixed, _positionWeight);
                }

                // Set hand rotation
                if (_rotationWeight >= 1f)
                    Hand._solverRotation = Rotation;
                else if (_rotationWeight > 0f)
                    Hand._solverRotation = Quaternion.Lerp(Hand._solverRotation, Rotation, _rotationWeight);
            }

            public override void ResetOffsets()
            {
                _handPositionOffset = Vector3.Zero;
            }

            public override void Write(ref Vector3[] solvedPositions, ref Quaternion[] solvedRotations)
            {
                if (_hasShoulder)
                {
                    solvedPositions[_index] = Shoulder._solverPosition;
                    solvedRotations[_index] = Shoulder._solverRotation;
                }

                solvedPositions[_index + 1] = UpperArm._solverPosition;
                solvedPositions[_index + 2] = Forearm._solverPosition;
                solvedPositions[_index + 3] = Hand._solverPosition;

                solvedRotations[_index + 1] = UpperArm._solverRotation;
                solvedRotations[_index + 2] = Forearm._solverRotation;
                solvedRotations[_index + 3] = Hand._solverRotation;
            }

            private static float DamperValue(float value, float min, float max, float weight = 1f)
            {
                float range = max - min;

                if (weight < 1f)
                {
                    float mid = max - range * 0.5f;
                    float v = value - mid;
                    v *= 0.5f;
                    value = mid + v;
                }

                value -= min;

                float t = (value / range).Clamp(0f, 1f);
                float tEased = Interp.Float(t, EFloatInterpolationMode.InOutQuintic);
                return Interp.Lerp(min, max, tEased);
            }

            private Vector3 GetBendNormal(Vector3 dir)
            {
                if (_bendGoal != null) _bendDirection = _bendGoal.WorldTranslation - _bones[1]._solverPosition;

                Vector3 armDir = _bones[0]._solverRotation.Rotate(_bones[0]._axis);

                Vector3 f = Globals.Down;
                Vector3 t = Quaternion.Inverse(_chestRotation).Rotate(dir.Normalized()) + Globals.Forward;
                Quaternion q = XRMath.RotationBetweenVectors(f, t);

                Vector3 b = q.Rotate(Globals.Backward);

                f = Quaternion.Inverse(_chestRotation).Rotate(armDir);
                t = Quaternion.Inverse(_chestRotation).Rotate(dir);
                q = XRMath.RotationBetweenVectors(f, t);
                b = q.Rotate(b);

                b = _chestRotation.Rotate(b);

                b += armDir;
                b -= Rotation.Rotate(_wristToPalmAxis);
                b -= Rotation.Rotate(_palmToThumbAxis) * 0.5f;

                if (_bendGoalWeight > 0f)
                    b = XRMath.Slerp(b, _bendDirection, _bendGoalWeight);
                
                if (_swivelOffset != 0f)
                    b = Quaternion.CreateFromAxisAngle(-dir, float.DegreesToRadians(_swivelOffset)).Rotate(b);

                return Vector3.Cross(b, dir);
            }

            private static void Visualize(VirtualBone bone1, VirtualBone bone2, VirtualBone bone3, ColorF4 color)
            {
                Engine.Rendering.Debug.RenderLine(bone1._solverPosition, bone2._solverPosition, color);
                Engine.Rendering.Debug.RenderLine(bone2._solverPosition, bone3._solverPosition, color);
            }
        }
    }
}
