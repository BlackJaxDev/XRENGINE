using Extensions;
using System;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;
using static XREngine.Engine.Rendering.Debug;

namespace XREngine.Components.Animation
{
    public partial class IKSolverVR
    {
        /// <summary>
        /// 4-segmented analytic leg chain.
        /// </summary>
        [Serializable]
        public class LegSolver(bool right) : BodyPart
        {
            private Transform? _target;
            /// <summary>
            /// The foot/toe target.
            /// This should not be the foot tracker itself,
            /// but a child SceneNode parented to it so you could adjust its position/rotation to match the orientation of the foot/toe bone.
            /// If a toe bone is assigned in the References,
            /// the solver will match the toe bone to this target. 
            /// If no toe bone assigned, foot bone will be used instead.
            /// </summary>
            public Transform? Target
            {
                get => _target;
                set => SetField(ref _target, value);
			}

            private float _positionWeight = 1.0f;
			/// <summary>
			/// Positional weight of the toe/foot target.
			/// Note that if you have nulled the target,
			/// the foot will still be pulled to the last position of the target until you set this value to 0.
			/// </summary>
			[Range(0.0f, 1.0f)]
            public float PositionWeight
            {
                get => _positionWeight;
                set => SetField(ref _positionWeight, value);
            }

			private float _rotationWeight = 1.0f;
			/// <summary>
			/// Rotational weight of the toe/foot target.
			/// Note that if you have nulled the target,
			/// the foot will still be rotated to the last rotation of the target until you set this value to 0.
			/// </summary>
			[Range(0.0f, 1.0f)]
			public float RotationWeight
            {
                get => _rotationWeight;
                set => SetField(ref _rotationWeight, value);
			}

			private Transform? _kneeTarget = null;
            /// <summary>
            /// The knee will be bent towards this Transform if 'Bend Goal Weight' > 0.
            /// </summary>
            public Transform? KneeTarget
            {
                get => _kneeTarget;
                set => SetField(ref _kneeTarget, value);
			}

			private float _kneeTargetWeight;
			/// <summary>
			/// If greater than 0, will bend the knee towards <see cref="KneeTarget"/>.
			/// </summary>
			[Range(0.0f, 1.0f)]
			public float KneeTargetWeight
            {
                get => _kneeTargetWeight;
                set => SetField(ref _kneeTargetWeight, value);
			}

			private float _swivelOffset = 0.0f;
            /// <summary>
            /// Angular offset of knee bending direction.
            /// </summary>
            [Range(-180.0f, 180.0f)]
			public float SwivelOffset
            {
                get => _swivelOffset;
                set => SetField(ref _swivelOffset, value);
			}

			private float _bendToTargetWeight = 0.5f;
			/// <summary>
			/// If 0, the bend plane will be locked to the rotation of the pelvis and rotating the foot will have no effect on the knee direction.
			/// If 1, to the target rotation of the leg so that the knee will bend towards the forward axis of the foot.
			/// Values in between will be slerped between the two.
			/// </summary>
			[Range(0.0f, 1.0f)]
			public float BendToTargetWeight
            {
                get => _bendToTargetWeight;
                set => SetField(ref _bendToTargetWeight, value);
			}

            private float _legLengthScale = 1.0f;
			/// <summary>
			/// Use this to make the leg shorter/longer.
			/// Works by displacement of foot and calf localPosition.
			/// </summary>
			[Range(0.01f, 2f)]
			public float LegLengthScale
            {
                get => _legLengthScale;
                set => SetField(ref _legLengthScale, value);
            }

			private AnimationCurve _stretchCurve = new();
            /// <summary>
            /// Evaluates stretching of the leg by target distance relative to leg length.
            /// Value at time 1 represents stretching amount at the point where distance to the target is equal to leg length.
            /// Value at time 1 represents stretching amount at the point where distance to the target is double the leg length.
            /// Value represents the amount of stretching.
            /// Linear stretching would be achieved with a linear curve going up by 45 degrees.
            /// Increase the range of stretching by moving the last key up and right at the same amount.
            /// Smoothing in the curve can help reduce knee snapping (start stretching the arm slightly before target distance reaches leg length).
            /// </summary>
            public AnimationCurve StretchCurve
            {
                get => _stretchCurve;
                set => SetField(ref _stretchCurve, value);
            }

            [NonSerialized]
            private Vector3 _ikPosition;
            /// <summary>
            /// Target position of the toe/foot. Will be overwritten if target is assigned.
            /// </summary>
            [HideInInspector]
            public Vector3 IKPosition
            {
                get => _ikPosition;
                set => _ikPosition = value;
            }

            [NonSerialized]
            private Quaternion _ikRotation = Quaternion.Identity;
            /// <summary>
            /// Target rotation of the toe/foot. Will be overwritten if target is assigned.
            /// </summary>
            [HideInInspector]
            public Quaternion IKRotation
            {
                get => _ikRotation;
                set => _ikRotation = value;
            }

            [NonSerialized]
            private Vector3 _footPositionOffset;
            /// <summary>
            /// Position offset of the toe/foot. Will be applied on top of target position and reset to Vector3.zero after each update.
            /// </summary>
            [HideInInspector]
            public Vector3 FootPositionOffset
            {
                get => _footPositionOffset;
                set => SetField(ref _footPositionOffset, value);
            }

            [NonSerialized]
            private Vector3 _heelPositionOffset;
            /// <summary>
            /// Position offset of the heel. Will be reset to Vector3.zero after each update.
            /// </summary>
            [HideInInspector]
            public Vector3 HeelPositionOffset
            {
                get => _heelPositionOffset;
                set => SetField(ref _heelPositionOffset, value);
            }

            [NonSerialized]
            private Quaternion _footRotationOffset = Quaternion.Identity;
            /// <summary>
            /// Rotation offset of the toe/foot. Will be reset to Quaternion.identity after each update.
            /// </summary>
            [HideInInspector]
            public Quaternion FootRotationOffset
            {
                get => _footRotationOffset;
                set => SetField(ref _footRotationOffset, value);
            }

            [NonSerialized]
            private float _currentLength;
            /// <summary>
            /// The length of the leg (calculated in last read).
            /// </summary>
            [HideInInspector]
            public float CurrentLength
            {
                get => _currentLength;
                set => SetField(ref _currentLength, value);
            }

            private bool _useAnimatedBendNormal;
            /// <summary>
            /// If true, will sample the leg bend angle each frame from the animation.
            /// </summary>
            [HideInInspector]
            public bool UseAnimatedBendNormal
            {
                get => _useAnimatedBendNormal;
                set => SetField(ref _useAnimatedBendNormal, value);
            }

            public Vector3 Position { get; private set; }
            public Quaternion Rotation { get; private set; }

            public bool HasToes { get; private set; }
            public VirtualBone Thigh => _bones[0];
            private VirtualBone Calf => _bones[1];
            private VirtualBone Foot => _bones[2];
            private VirtualBone Toes => _bones[3];
            public VirtualBone LastBone => _bones[^1];

            public override void Visualize(ColorF4 color)
            {
                base.Visualize(color);

                if (Target is not null)
                {
                    RenderText(Target.WorldTranslation, "Target", ColorF4.Black);
                    RenderPoint(Target.WorldTranslation, ColorF4.Black);
                }

                if (KneeTarget is not null)
                {
                    RenderText(KneeTarget.WorldTranslation, "Knee Target", ColorF4.Black);
                    RenderPoint(KneeTarget.WorldTranslation, ColorF4.Black);
                }

                RenderText(Thigh.SolverPosition, "Thigh", ColorF4.Black);
                RenderText(Calf.SolverPosition, "Calf", ColorF4.Black);
                RenderText(Foot.SolverPosition, "Foot", ColorF4.Black);
                if (HasToes)
                    RenderText(Toes.SolverPosition, "Toes", ColorF4.Black);
            }

            public Vector3 ThighRelativeToHips { get; private set; }

            private Vector3 _footPosition;
            private Quaternion _footRotation = Quaternion.Identity;
            private Vector3 _bendNormal;
            private Quaternion _calfRelToThigh = Quaternion.Identity;
            private Quaternion _thighRelToFoot = Quaternion.Identity;

            public Vector3 BendNormalRelToPelvis { get; set; }
            public Vector3 BendNormalRelToTarget { get; set; }

            protected override void OnRead(SolverTransforms transforms)
            {
                if (_initialized)
                    return;
                
                var side = right ? transforms.Right : transforms.Left;

                if (HasToes = transforms.HasToes)
                {
                    _bones =
                    [
                        new(side.Leg.Leg),
                        new(side.Leg.Knee),
                        new(side.Leg.Foot),
                        new(side.Leg.Toes),
                    ];

                    IKPosition = side.Leg.Toes.Input.Translation;
                    IKRotation = side.Leg.Toes.Input.Rotation;
                }
                else
                {
                    _bones =
                    [
                        new(side.Leg.Leg),
                        new(side.Leg.Knee),
                        new(side.Leg.Foot),
                    ];

                    IKPosition = side.Leg.Foot.Input.Translation;
                    IKRotation = side.Leg.Foot.Input.Rotation;
                }

                Vector3 calfPos = side.Leg.Knee.Input.Translation;
                Vector3 thighPos = side.Leg.Leg.Input.Translation;
                Vector3 footPos = side.Leg.Foot.Input.Translation;
                _bendNormal = Vector3.Cross(calfPos - thighPos, footPos - calfPos);
                //bendNormal = rotations[0] * Vector3.right; // Use this to make the knees bend towards root.forward

                BendNormalRelToPelvis = Quaternion.Inverse(_rootRotation).Rotate(_bendNormal);
                BendNormalRelToTarget = Quaternion.Inverse(IKRotation).Rotate(_bendNormal);

                Rotation = IKRotation;
            }

            public override void PreSolve(float scale)
            {
                if (_target != null)
                {
                    _target.RecalculateMatrices(true);
                    IKPosition = _target.WorldTranslation;
                    IKRotation = _target.WorldRotation;
                }

                _footPosition = Foot.SolverPosition;
                _footRotation = Foot.SolverRotation;

                Position = LastBone.SolverPosition;
                Rotation = LastBone.SolverRotation;

                if (_rotationWeight > 0.0f)
                    ApplyRotationOffset(XRMath.FromToRotation(Rotation, IKRotation), _rotationWeight);
                
                if (_positionWeight > 0.0f)
                    ApplyPositionOffset(IKPosition - Position, _positionWeight);
                
                ThighRelativeToHips = Quaternion.Inverse(_rootRotation).Rotate(Thigh.SolverPosition - _rootPosition);
                _calfRelToThigh = Quaternion.Inverse(Thigh.SolverRotation) * Calf.SolverRotation;
                _thighRelToFoot = Quaternion.Inverse(LastBone.SolverRotation) * Thigh.SolverRotation;

                // Calculate bend plane normal
                if (_useAnimatedBendNormal)
                    _bendNormal = Vector3.Cross(Calf.SolverPosition - Thigh.SolverPosition, Foot.SolverPosition - Calf.SolverPosition);
                else if (_bendToTargetWeight <= 0.0f)
                    _bendNormal = _rootRotation.Rotate(BendNormalRelToPelvis);
                else if (_bendToTargetWeight >= 1.0f)
                    _bendNormal = Rotation.Rotate(BendNormalRelToTarget);
                else
                    _bendNormal = XRMath.Slerp(_rootRotation.Rotate(BendNormalRelToPelvis), Rotation.Rotate(BendNormalRelToTarget), _bendToTargetWeight);
                _bendNormal = _bendNormal.Normalized();
            }

            public override void ApplyOffsets(float scale)
            {
                ApplyPositionOffset(_footPositionOffset, 1.0f);
                ApplyRotationOffset(_footRotationOffset, 1.0f);

                // Heel position offset
                Quaternion fromTo = XRMath.RotationBetweenVectors(_footPosition - Position, _footPosition + _heelPositionOffset - Position);
                _footPosition = Position + fromTo.Rotate(_footPosition - Position);
                _footRotation = fromTo * _footRotation;

                // Bend normal offset
                float bAngle = 0.0f;

                if (_kneeTarget != null && _kneeTargetWeight > 0.0f)
                {
                    _kneeTarget.RecalculateMatrices(true);
                    var thighToKnee = _kneeTarget.WorldTranslation - Thigh.SolverPosition;
                    var thighToPos = Position - Thigh.SolverPosition;
                    var footToThigh = Thigh.SolverPosition - Foot.SolverPosition;

                    Vector3 b = Vector3.Cross(thighToKnee, thighToPos);
                    Quaternion l = XRMath.LookRotation(_bendNormal, footToThigh);
                    Vector3 bRelative = Quaternion.Inverse(l).Rotate(b);
                    bAngle = float.RadiansToDegrees(MathF.Atan2(bRelative.X, bRelative.Z)) * _kneeTargetWeight;
                }
                float sO = _swivelOffset + bAngle;
                if (sO != 0.0f)
                {
                    sO = float.DegreesToRadians(sO);
                    var lastBoneToThigh = Thigh.SolverPosition - LastBone.SolverPosition;

                    _bendNormal = Quaternion.CreateFromAxisAngle(lastBoneToThigh, sO).Rotate(_bendNormal);
                    Thigh.SolverRotation = Quaternion.CreateFromAxisAngle(Thigh.SolverRotation.Rotate(Thigh.Axis), -sO) * Thigh.SolverRotation;
                }
            }

            // Foot position offset
            private void ApplyPositionOffset(Vector3 offset, float weight)
            {
                if (weight <= 0.0f)
                    return;

                offset *= weight;

                // Foot position offset
                _footPosition += offset;
                Position += offset;
            }

            // Foot rotation offset
            private void ApplyRotationOffset(Quaternion offset, float weight)
            {
                if (weight <= 0.0f)
                    return;

                if (weight < 1.0f)
                    offset = Quaternion.Lerp(Quaternion.Identity, offset, weight);
                
                _footRotation = offset * _footRotation;
                Rotation = offset * Rotation;
                _bendNormal = offset.Rotate(_bendNormal).Normalized();
                _footPosition = Position + offset.Rotate(_footPosition - Position);
            }

            public void Solve(bool stretch)
            {
                if (stretch && _quality < EQuality.Semi)
                    Stretching();

                // Foot pass
                VirtualBone.SolveTrigonometric(_bones, 0, 1, 2, _footPosition, _bendNormal, 1.0f);

                // Rotate foot back to where it was before the last solving
                RotateTo(Foot, _footRotation);

                // Toes pass
                if (!HasToes)
                {
                    FixTwistRotations();
                    return;
                }

                Vector3 b = Vector3.Cross(
                    Foot.SolverPosition - Thigh.SolverPosition,
                    Toes.SolverPosition - Foot.SolverPosition
                    ).Normalized();

                VirtualBone.SolveTrigonometric(_bones, 0, 2, 3, Position, b, 1.0f);

                // Fix thigh twist relative to target rotation
                FixTwistRotations();

                // Keep toe rotation fixed
                Toes.SolverRotation = Rotation;
            }

            private void FixTwistRotations()
            {
                if (Quality >= EQuality.Semi)
                    return;
                
                if (_bendToTargetWeight > 0.0f)
                {
                    // Fix thigh twist relative to target rotation
                    Quaternion thighRotation = Rotation * _thighRelToFoot;
                    Quaternion f = XRMath.RotationBetweenVectors(thighRotation.Rotate(Thigh.Axis), Calf.SolverPosition - Thigh.SolverPosition);
                    if (_bendToTargetWeight < 1.0f)
                        Thigh.SolverRotation = Quaternion.Slerp(Thigh.SolverRotation, f * thighRotation, _bendToTargetWeight);
                    else
                        Thigh.SolverRotation = f * thighRotation;
                }

                // Fix calf twist relative to thigh
                Quaternion calfRotation = Thigh.SolverRotation * _calfRelToThigh;
                Quaternion fromTo = XRMath.RotationBetweenVectors(calfRotation.Rotate(Calf.Axis), Foot.SolverPosition - Calf.SolverPosition);
                Calf.SolverRotation = fromTo * calfRotation;
            }

            private void Stretching()
            {
                // Adjusting leg length
                float legLength = Thigh.Length + Calf.Length;
                Vector3 kneeAdd = Vector3.Zero;
                Vector3 footAdd = Vector3.Zero;

                if (_legLengthScale != 1.0f)
                {
                    legLength *= _legLengthScale;
                    kneeAdd = (Calf.SolverPosition - Thigh.SolverPosition) * (_legLengthScale - 1.0f);// * positionWeight;
                    footAdd = (Foot.SolverPosition - Calf.SolverPosition) * (_legLengthScale - 1.0f);// * positionWeight;
                    Calf.SolverPosition += kneeAdd;
                    Foot.SolverPosition += kneeAdd + footAdd;
                    if (HasToes)
                        Toes.SolverPosition += kneeAdd + footAdd;
                }

                // Stretching
                float distanceToTarget = Vector3.Distance(Thigh.SolverPosition, _footPosition);
                float stretchF = distanceToTarget / legLength;

                float m = _stretchCurve.Evaluate(stretchF);// * positionWeight; mlp by positionWeight enables stretching only for foot trackers, but not for built-in or animated locomotion

                kneeAdd = (Calf.SolverPosition - Thigh.SolverPosition) * m;
                footAdd = (Foot.SolverPosition - Calf.SolverPosition) * m;

                Calf.SolverPosition += kneeAdd;
                Foot.SolverPosition += kneeAdd + footAdd;
                if (HasToes)
                    Toes.SolverPosition += kneeAdd + footAdd;
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
