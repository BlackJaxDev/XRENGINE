using Extensions;
using System;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Components.Animation
{
    public partial class IKSolverVR
    {        
        /// <summary>
        /// 4-segmented analytic leg chain.
        /// </summary>
        [System.Serializable]
        public class Leg : BodyPart
        {
            /// <summary>
            /// The foot/toe target.
            /// This should not be the foot tracker itself,
            /// but a child SceneNode parented to it so you could adjust its position/rotation to match the orientation of the foot/toe bone.
            /// If a toe bone is assigned in the References,
            /// the solver will match the toe bone to this target. 
            /// If no toe bone assigned, foot bone will be used instead.
            /// </summary>
            public Transform? _target;

            /// <summary>
            /// Positional weight of the toe/foot target.
            /// Note that if you have nulled the target,
            /// the foot will still be pulled to the last position of the target until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _positionWeight;

            /// <summary>
            /// Rotational weight of the toe/foot target.
            /// Note that if you have nulled the target,
            /// the foot will still be rotated to the last rotation of the target until you set this value to 0.
            /// </summary>
            [Range(0f, 1f)]
            public float _rotationWeight;

            /// <summary>
            /// The knee will be bent towards this Transform if 'Bend Goal Weight' > 0.
            /// </summary>
            public Transform? _bendGoal;

            /// <summary>
            /// If greater than 0, will bend the knee towards the 'Bend Goal' Transform.
            /// </summary>
            [Range(0f, 1f)]
            public float _bendGoalWeight;

            /// <summary>
            /// Angular offset of knee bending direction.
            /// </summary>
            [Range(-180f, 180f)]
            public float _swivelOffset;

            /// <summary>
            /// If 0, the bend plane will be locked to the rotation of the pelvis and rotating the foot will have no effect on the knee direction.
            /// If 1, to the target rotation of the leg so that the knee will bend towards the forward axis of the foot.
            /// Values in between will be slerped between the two.
            /// </summary>
            [Range(0f, 1f)]
            public float _bendToTargetWeight = 0.5f;

            /// <summary>
            /// Use this to make the leg shorter/longer.
            /// Works by displacement of foot and calf localPosition.
            /// </summary>
            [Range(0.01f, 2f)]
            public float _legLengthMlp = 1f;

            /// <summary>
            /// Evaluates stretching of the leg by target distance relative to leg length.
            /// Value at time 1 represents stretching amount at the point where distance to the target is equal to leg length.
            /// Value at time 1 represents stretching amount at the point where distance to the target is double the leg length.
            /// Value represents the amount of stretching.
            /// Linear stretching would be achieved with a linear curve going up by 45 degrees.
            /// Increase the range of stretching by moving the last key up and right at the same amount.
            /// Smoothing in the curve can help reduce knee snapping (start stretching the arm slightly before target distance reaches leg length).
            /// </summary>
            public AnimationCurve _stretchCurve = new();

            /// <summary>
            /// Target position of the toe/foot. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 IKPosition;

            /// <summary>
            /// Target rotation of the toe/foot. Will be overwritten if target is assigned.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion IKRotation = Quaternion.Identity;

            /// <summary>
            /// Position offset of the toe/foot. Will be applied on top of target position and reset to Vector3.zero after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _footPositionOffset;

            /// <summary>
            /// Position offset of the heel. Will be reset to Vector3.zero after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Vector3 _heelPositionOffset;

            /// <summary>
            /// Rotation offset of the toe/foot. Will be reset to Quaternion.identity after each update.
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public Quaternion _footRotationOffset = Quaternion.Identity;

            /// <summary>
            /// The length of the leg (calculated in last read).
            /// </summary>
            [NonSerialized]
            [HideInInspector]
            public float _currentMag;

            /// <summary>
            /// If true, will sample the leg bend angle each frame from the animation.
            /// </summary>
            [HideInInspector]
            public bool _useAnimatedBendNormal;

            public Vector3 Position { get; private set; }
            public Quaternion Rotation { get; private set; }
            public bool HasToes { get; private set; }
            public VirtualBone Thigh => _bones[0];
            private VirtualBone Calf => _bones[1];
            private VirtualBone Foot => _bones[2];
            private VirtualBone Toes => _bones[3];
            public VirtualBone LastBone => _bones[^1];
            public Vector3 ThighRelativeToPelvis { get; private set; }

            private Vector3 _footPosition;
            private Quaternion _footRotation = Quaternion.Identity;
            private Vector3 _bendNormal;
            private Quaternion _calfRelToThigh = Quaternion.Identity;
            private Quaternion _thighRelToFoot = Quaternion.Identity;
            public Vector3 BendNormalRelToPelvis { get; set; }
            public Vector3 BendNormalRelToTarget { get; set; }

            protected override void OnRead(Vector3[] positions, Quaternion[] rotations, bool hasChest, bool hasNeck, bool hasShoulders, bool hasToes, bool hasLegs, int rootIndex, int index)
            {
                Vector3 thighPos = positions[index];
                Quaternion thighRot = rotations[index];
                Vector3 calfPos = positions[index + 1];
                Quaternion calfRot = rotations[index + 1];
                Vector3 footPos = positions[index + 2];
                Quaternion footRot = rotations[index + 2];
                Vector3 toePos = positions[index + 3];
                Quaternion toeRot = rotations[index + 3];

                if (!_initialized)
                {
                    this.HasToes = hasToes;
                    _bones = new VirtualBone[hasToes ? 4 : 3];

                    if (hasToes)
                    {
                        _bones[0] = new VirtualBone(thighPos, thighRot);
                        _bones[1] = new VirtualBone(calfPos, calfRot);
                        _bones[2] = new VirtualBone(footPos, footRot);
                        _bones[3] = new VirtualBone(toePos, toeRot);

                        IKPosition = toePos;
                        IKRotation = toeRot;
                    }
                    else
                    {
                        _bones[0] = new VirtualBone(thighPos, thighRot);
                        _bones[1] = new VirtualBone(calfPos, calfRot);
                        _bones[2] = new VirtualBone(footPos, footRot);

                        IKPosition = footPos;
                        IKRotation = footRot;
                    }

                    _bendNormal = Vector3.Cross(calfPos - thighPos, footPos - calfPos);
                    //bendNormal = rotations[0] * Vector3.right; // Use this to make the knees bend towards root.forward

                    BendNormalRelToPelvis = Quaternion.Inverse(_rootRotation).Rotate(_bendNormal);
                    BendNormalRelToTarget = Quaternion.Inverse(IKRotation).Rotate(_bendNormal);

                    Rotation = IKRotation;
                }

                if (hasToes)
                {
                    _bones[0].Read(thighPos, thighRot);
                    _bones[1].Read(calfPos, calfRot);
                    _bones[2].Read(footPos, footRot);
                    _bones[3].Read(toePos, toeRot);
                }
                else
                {
                    _bones[0].Read(thighPos, thighRot);
                    _bones[1].Read(calfPos, calfRot);
                    _bones[2].Read(footPos, footRot);
                }
            }

            public override void PreSolve(float scale)
            {
                if (_target != null)
                {
                    IKPosition = _target.WorldTranslation;
                    IKRotation = _target.WorldRotation;
                }

                _footPosition = Foot._solverPosition;
                _footRotation = Foot._solverRotation;

                Position = LastBone._solverPosition;
                Rotation = LastBone._solverRotation;

                if (_rotationWeight > 0f)
                    ApplyRotationOffset(XRMath.FromToRotation(Rotation, IKRotation), _rotationWeight);
                
                if (_positionWeight > 0f)
                    ApplyPositionOffset(IKPosition - Position, _positionWeight);
                
                ThighRelativeToPelvis = Quaternion.Inverse(_rootRotation).Rotate(Thigh._solverPosition - _rootPosition);
                _calfRelToThigh = Quaternion.Inverse(Thigh._solverRotation) * Calf._solverRotation;
                _thighRelToFoot = Quaternion.Inverse(LastBone._solverRotation) * Thigh._solverRotation;

                // Calculate bend plane normal
                if (_useAnimatedBendNormal)
                    _bendNormal = Vector3.Cross(Calf._solverPosition - Thigh._solverPosition, Foot._solverPosition - Calf._solverPosition);
                else if (_bendToTargetWeight <= 0f)
                    _bendNormal = _rootRotation.Rotate(BendNormalRelToPelvis);
                else if (_bendToTargetWeight >= 1f)
                    _bendNormal = Rotation.Rotate(BendNormalRelToTarget);
                else
                    _bendNormal = XRMath.Slerp(_rootRotation.Rotate(BendNormalRelToPelvis), Rotation.Rotate(BendNormalRelToTarget), _bendToTargetWeight);
                _bendNormal = _bendNormal.Normalized();
            }

            public override void ApplyOffsets(float scale)
            {
                ApplyPositionOffset(_footPositionOffset, 1f);
                ApplyRotationOffset(_footRotationOffset, 1f);

                // Heel position offset
                Quaternion fromTo = XRMath.RotationBetweenVectors(_footPosition - Position, _footPosition + _heelPositionOffset - Position);
                _footPosition = Position + fromTo.Rotate(_footPosition - Position);
                _footRotation = fromTo * _footRotation;

                // Bend normal offset
                float bAngle = 0f;

                if (_bendGoal != null && _bendGoalWeight > 0f)
                {
                    Vector3 b = Vector3.Cross(_bendGoal.WorldTranslation - Thigh._solverPosition, Position - Thigh._solverPosition);
                    Quaternion l = XRMath.LookRotation(_bendNormal, Thigh._solverPosition - Foot._solverPosition);
                    Vector3 bRelative = Quaternion.Inverse(l).Rotate(b);
                    bAngle = float.RadiansToDegrees(MathF.Atan2(bRelative.X, bRelative.Z)) * _bendGoalWeight;
                }
                float sO = _swivelOffset + bAngle;
                if (sO != 0f)
                {
                    sO = float.DegreesToRadians(sO);
                    _bendNormal = Quaternion.CreateFromAxisAngle(Thigh._solverPosition - LastBone._solverPosition, sO).Rotate(_bendNormal);
                    Thigh._solverRotation = Quaternion.CreateFromAxisAngle(Thigh._solverRotation.Rotate(Thigh._axis), -sO) * Thigh._solverRotation;
                }
            }

            // Foot position offset
            private void ApplyPositionOffset(Vector3 offset, float weight)
            {
                if (weight <= 0f)
                    return;

                offset *= weight;

                // Foot position offset
                _footPosition += offset;
                Position += offset;
            }

            // Foot rotation offset
            private void ApplyRotationOffset(Quaternion offset, float weight)
            {
                if (weight <= 0f)
                    return;

                if (weight < 1f)
                    offset = Quaternion.Lerp(Quaternion.Identity, offset, weight);
                
                _footRotation = offset * _footRotation;
                Rotation = offset * Rotation;
                _bendNormal = offset.Rotate(_bendNormal);
                _footPosition = Position + offset.Rotate(_footPosition - Position);
            }

            public void Solve(bool stretch)
            {
                if (stretch && _lod < 1)
                    Stretching();

                // Foot pass
                VirtualBone.SolveTrigonometric(_bones, 0, 1, 2, _footPosition, _bendNormal, 1f);

                // Rotate foot back to where it was before the last solving
                RotateTo(Foot, _footRotation);

                // Toes pass
                if (!HasToes)
                {
                    FixTwistRotations();
                    return;
                }

                Vector3 b = Vector3.Cross(
                    Foot._solverPosition - Thigh._solverPosition,
                    Toes._solverPosition - Foot._solverPosition
                    ).Normalized();

                VirtualBone.SolveTrigonometric(_bones, 0, 2, 3, Position, b, 1f);

                // Fix thigh twist relative to target rotation
                FixTwistRotations();

                // Keep toe rotation fixed
                Toes._solverRotation = Rotation;
            }

            private void FixTwistRotations()
            {
                if (_lod >= 1)
                    return;
                
                if (_bendToTargetWeight > 0f)
                {
                    // Fix thigh twist relative to target rotation
                    Quaternion thighRotation = Rotation * _thighRelToFoot;
                    Quaternion f = XRMath.RotationBetweenVectors(thighRotation.Rotate(Thigh._axis), Calf._solverPosition - Thigh._solverPosition);
                    if (_bendToTargetWeight < 1f)
                        Thigh._solverRotation = Quaternion.Slerp(Thigh._solverRotation, f * thighRotation, _bendToTargetWeight);
                    else
                        Thigh._solverRotation = f * thighRotation;
                }

                // Fix calf twist relative to thigh
                Quaternion calfRotation = Thigh._solverRotation * _calfRelToThigh;
                Quaternion fromTo = XRMath.RotationBetweenVectors(calfRotation.Rotate(Calf._axis), Foot._solverPosition - Calf._solverPosition);
                Calf._solverRotation = fromTo * calfRotation;
            }

            private void Stretching()
            {
                // Adjusting leg length
                float legLength = Thigh._length + Calf._length;
                Vector3 kneeAdd = Vector3.Zero;
                Vector3 footAdd = Vector3.Zero;

                if (_legLengthMlp != 1f)
                {
                    legLength *= _legLengthMlp;
                    kneeAdd = (Calf._solverPosition - Thigh._solverPosition) * (_legLengthMlp - 1f);// * positionWeight;
                    footAdd = (Foot._solverPosition - Calf._solverPosition) * (_legLengthMlp - 1f);// * positionWeight;
                    Calf._solverPosition += kneeAdd;
                    Foot._solverPosition += kneeAdd + footAdd;
                    if (HasToes)
                        Toes._solverPosition += kneeAdd + footAdd;
                }

                // Stretching
                float distanceToTarget = Vector3.Distance(Thigh._solverPosition, _footPosition);
                float stretchF = distanceToTarget / legLength;

                float m = _stretchCurve.Evaluate(stretchF);// * positionWeight; mlp by positionWeight enables stretching only for foot trackers, but not for built-in or animated locomotion

                kneeAdd = (Calf._solverPosition - Thigh._solverPosition) * m;
                footAdd = (Foot._solverPosition - Calf._solverPosition) * m;

                Calf._solverPosition += kneeAdd;
                Foot._solverPosition += kneeAdd + footAdd;
                if (HasToes)
                    Toes._solverPosition += kneeAdd + footAdd;
            }

            public override void Write(ref Vector3[] solvedPositions, ref Quaternion[] solvedRotations)
            {
                solvedRotations[_index] = Thigh._solverRotation;
                solvedRotations[_index + 1] = Calf._solverRotation;
                solvedRotations[_index + 2] = Foot._solverRotation;

                solvedPositions[_index] = Thigh._solverPosition;
                solvedPositions[_index + 1] = Calf._solverPosition;
                solvedPositions[_index + 2] = Foot._solverPosition;

                if (HasToes)
                {
                    solvedRotations[_index + 3] = Toes._solverRotation;
                    solvedPositions[_index + 3] = Toes._solverPosition;
                }
            }

            public override void ResetOffsets()
            {
                _footPositionOffset = Vector3.Zero;
                _footRotationOffset = Quaternion.Identity;
                _heelPositionOffset = Vector3.Zero;
            }
        }
    }
}
