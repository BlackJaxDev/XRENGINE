using Extensions;
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

			private float _bendToTargetWeight = 1.0f;
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
                set => SetField(ref _ikPosition, value);
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
                set => SetField(ref _ikRotation, value);
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

            public Vector3 TargetPosition { get; private set; }
            public Quaternion TargetRotation { get; private set; }

            public bool HasToes { get; private set; }
            public VirtualBone Thigh => _bones[0];
            private VirtualBone Calf => _bones[1];
            private VirtualBone Foot => _bones[2];
            private VirtualBone Toes => _bones[3];
            public VirtualBone LastBone => _bones[^1];

            public override void Visualize(ColorF4 color)
            {
                if (!_initialized)
                    return;

                base.Visualize(color);

                string side = right ? "Right" : "Left";

                if (Target is not null)
                {
                    //RenderCoordinateSystem(Target.WorldMatrix);
                    RenderPoint(Target.WorldTranslation, ColorF4.Black);
                    RenderText(Target.WorldTranslation, $"{side} Foot Target", ColorF4.Black);
                }

                if (KneeTarget is not null)
                {
                    //RenderCoordinateSystem(KneeTarget.WorldMatrix);
                    RenderPoint(KneeTarget.WorldTranslation, ColorF4.Black);
                    RenderText(KneeTarget.WorldTranslation, $"{side} Knee Target", ColorF4.Black);
                }

                //RenderCoordinateSystem(Thigh.SolverPosition, Thigh.SolverRotation);
                RenderText(Thigh.SolverPosition, $"{side} Thigh", ColorF4.Black);

                //RenderCoordinateSystem(Calf.SolverPosition, Calf.SolverRotation);
                RenderText(Calf.SolverPosition, $"{side} Calf", ColorF4.Black);

                //RenderCoordinateSystem(Foot.SolverPosition, Foot.SolverRotation);
                RenderText(Foot.SolverPosition, $"{side} Foot", ColorF4.Black);

                if (HasToes)
                {
                    //RenderCoordinateSystem(Toes.SolverPosition, Toes.SolverRotation);
                    RenderText(Toes.SolverPosition, $"{side} Toes", ColorF4.Black);
                }
            }

            public Vector3 ThighRelativeToHips { get; private set; }

            private Vector3 _footPosition;
            private Quaternion _footRotation = Quaternion.Identity;
            private Vector3 _bendNormal;
            private Quaternion _calfRelToThigh = Quaternion.Identity;
            private Quaternion _thighRelToFoot = Quaternion.Identity;
            private Vector3 _bendNormalRelToHips;
            private Vector3 _bendNormalRelToTarget;

            protected override void OnRead(SolverTransforms transforms)
            {
                if (!_initialized)
                    InitializeTransforms(transforms);
            }

            private void InitializeTransforms(SolverTransforms transforms)
            {
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

                    IKPosition = side.Leg.Toes.InputWorld.Translation;
                    IKRotation = side.Leg.Toes.InputWorld.Rotation;
                }
                else
                {
                    _bones =
                    [
                        new(side.Leg.Leg),
                        new(side.Leg.Knee),
                        new(side.Leg.Foot),
                    ];

                    IKPosition = side.Leg.Foot.InputWorld.Translation;
                    IKRotation = side.Leg.Foot.InputWorld.Rotation;
                }

                Vector3 calfPos = side.Leg.Knee.InputWorld.Translation;
                Vector3 thighPos = side.Leg.Leg.InputWorld.Translation;
                Vector3 footPos = side.Leg.Foot.InputWorld.Translation;

                Vector3 thighToCalf = calfPos - thighPos;
                Vector3 calfToFoot = footPos - calfPos;

                float dot = Vector3.Dot(thighToCalf, calfToFoot);
                //If same direction, then the leg is straight and we can use the default bend normal
                if (dot > 0.999f || dot < -0.999f)
                    _bendNormal = _rootRotation.Rotate(Globals.Left).Normalized(); // Make knees bend towards root.forward
                else
                    _bendNormal = Vector3.Cross(calfToFoot, thighToCalf).Normalized();

                _bendNormalRelToHips = Quaternion.Inverse(_rootRotation).Rotate(_bendNormal);
                _bendNormalRelToTarget = Quaternion.Inverse(IKRotation).Rotate(_bendNormal);
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

                TargetPosition = LastBone.SolverPosition;
                TargetRotation = LastBone.SolverRotation;

                if (_rotationWeight > 0.0f)
                    ApplyRotationOffset(XRMath.FromToRotation(TargetRotation, IKRotation), _rotationWeight);
                
                if (_positionWeight > 0.0f)
                    ApplyPositionOffset(IKPosition - TargetPosition, _positionWeight);
                
                ThighRelativeToHips = Quaternion.Inverse(_rootRotation).Rotate(Thigh.SolverPosition - _rootPosition);
                _calfRelToThigh = Quaternion.Inverse(Thigh.SolverRotation) * Calf.SolverRotation;
                _thighRelToFoot = Quaternion.Inverse(LastBone.SolverRotation) * Thigh.SolverRotation;

                // Calculate bend plane normal
                Vector3 bendNormal;
                if (_useAnimatedBendNormal)
                {
                    Vector3 thighToCalf = Calf.SolverPosition - Thigh.SolverPosition;
                    Vector3 calfToFoot = Foot.SolverPosition - Calf.SolverPosition;
                    bendNormal = Vector3.Cross(thighToCalf, calfToFoot);
                }
                else
                {
                    Vector3 HipsRelNormal() => _rootRotation.Rotate(_bendNormalRelToHips);
                    Vector3 TargetRelNormal() => TargetRotation.Rotate(_bendNormalRelToTarget);
                    bendNormal = _bendToTargetWeight switch
                    {
                        <= 0.0f => HipsRelNormal(),
                        >= 1.0f => TargetRelNormal(),
                        _ => XRMath.Slerp(HipsRelNormal(), TargetRelNormal(), _bendToTargetWeight),
                    };
                }
                _bendNormal = bendNormal.Normalized();
            }

            public override void ApplyOffsets(float scale)
            {
                ApplyPositionOffset(_footPositionOffset, 1.0f);
                ApplyRotationOffset(_footRotationOffset, 1.0f);

                // Heel position offset
                Vector3 targetToFoot = _footPosition - TargetPosition;
                Vector3 targetToHeel = _footPosition + _heelPositionOffset - TargetPosition;
                Quaternion fromTo = XRMath.RotationBetweenVectors(targetToFoot, targetToHeel);
                _footPosition = TargetPosition + fromTo.Rotate(targetToFoot);
                _footRotation = fromTo * _footRotation;

                // Bend normal offset
                float bAngle = 0.0f;

                if (_kneeTarget != null && _kneeTargetWeight > 0.0f)
                {
                    _kneeTarget.RecalculateMatrices(true);
                    var thighToKnee = _kneeTarget.WorldTranslation - Thigh.SolverPosition;
                    var thighToPos = TargetPosition - Thigh.SolverPosition;
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
                TargetPosition += offset;
            }

            // Foot rotation offset
            private void ApplyRotationOffset(Quaternion offset, float weight)
            {
                if (weight <= 0.0f)
                    return;

                if (weight < 1.0f)
                    offset = Quaternion.Lerp(Quaternion.Identity, offset, weight);
                
                _footRotation = offset * _footRotation;
                TargetRotation = offset * TargetRotation;
                _bendNormal = offset.Rotate(_bendNormal).Normalized();
                _footPosition = TargetPosition + offset.Rotate(_footPosition - TargetPosition);
            }

            public void Solve(bool stretch)
            {
                if (stretch && _quality < EQuality.Semi)
                    StretchLeg();

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

                //SolveToes();

                // Fix thigh twist relative to target rotation
                FixTwistRotations();

                // Keep toe rotation fixed
                Toes.SolverRotation = TargetRotation;
            }

            private void SolveToes()
            {
                Vector3 thighToFoot = Foot.SolverPosition - Thigh.SolverPosition;
                Vector3 footToToes = Toes.SolverPosition - Foot.SolverPosition;
                Vector3 b = Vector3.Cross(thighToFoot, footToToes).Normalized();
                VirtualBone.SolveTrigonometric(_bones, 0, 2, 3, TargetPosition, b, 1.0f);
            }

            private void FixTwistRotations()
            {
                if (Quality >= EQuality.Semi)
                    return;
                
                if (_bendToTargetWeight > 0.0f)
                {
                    // Fix thigh twist relative to target rotation
                    Quaternion thighRotation = TargetRotation * _thighRelToFoot;
                    Vector3 thighToCalf = Calf.SolverPosition - Thigh.SolverPosition;
                    Quaternion f = XRMath.RotationBetweenVectors(thighRotation.Rotate(Thigh.Axis), thighToCalf);
                    Thigh.SolverRotation = _bendToTargetWeight < 1.0f 
                        ? Quaternion.Lerp(Thigh.SolverRotation, f * thighRotation, _bendToTargetWeight) : 
                        f * thighRotation;
                }

                // Fix calf twist relative to thigh
                Quaternion calfRotation = Thigh.SolverRotation * _calfRelToThigh;
                Vector3 calfToFoot = Foot.SolverPosition - Calf.SolverPosition;
                Quaternion fromTo = XRMath.RotationBetweenVectors(calfRotation.Rotate(Calf.Axis), calfToFoot);
                Calf.SolverRotation = fromTo * calfRotation;
            }

            private void StretchLeg()
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
