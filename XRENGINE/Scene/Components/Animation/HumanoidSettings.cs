using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Components.Animation
{
    public class HumanoidSettings : XRBase
    {
        private Dictionary<EHumanoidValue, float> _currentValues = [];
        public Dictionary<EHumanoidValue, float> CurrentValues
        {
            get => _currentValues;
            set => SetField(ref _currentValues, value);
        }

        public void SetValue(EHumanoidValue value, float amount)
        {
            if (!CurrentValues.TryAdd(value, amount))
                CurrentValues[value] = amount;
        }
        public float GetValue(EHumanoidValue value)
            => CurrentValues.TryGetValue(value, out var amount) ? amount : 0.0f;

        private Vector2 _leftEyeDownUpRange = new(-1.0f, 1.0f);
        public Vector2 LeftEyeDownUpRange
        {
            get => _leftEyeDownUpRange;
            set => SetField(ref _leftEyeDownUpRange, value);
        }

        private Vector2 _leftEyeInOutRange = new(-1.0f, 1.0f);
        public Vector2 LeftEyeInOutRange
        {
            get => _leftEyeInOutRange;
            set => SetField(ref _leftEyeInOutRange, value);
        }

        private Vector2 _rightEyeDownUpRange = new(-1.0f, 1.0f);
        public Vector2 RightEyeDownUpRange
        {
            get => _rightEyeDownUpRange;
            set => SetField(ref _rightEyeDownUpRange, value);
        }

        private Vector2 _rightEyeInOutRange = new(-1.0f, 1.0f);
        public Vector2 RightEyeInOutRange
        {
            get => _rightEyeInOutRange;
            set => SetField(ref _rightEyeInOutRange, value);
        }

        // Humanoid muscle curves are normalized in [-1, 1].
        // We first scale the muscle values by this multiplier, then map them into per-channel rotation ranges.
        private float _muscleInputScale = 1.0f;
        public float MuscleInputScale
        {
            get => _muscleInputScale;
            set => SetField(ref _muscleInputScale, value);
        }

        private Dictionary<EHumanoidValue, Vector2> _muscleRotationDegRanges = [];
        /// <summary>
        /// Optional per-channel rotation ranges in degrees (min, max). Keyed by humanoid muscle enum.
        /// If a key is missing, the runtime falls back to built-in defaults.
        /// </summary>
        public Dictionary<EHumanoidValue, Vector2> MuscleRotationDegRanges
        {
            get => _muscleRotationDegRanges;
            set => SetField(ref _muscleRotationDegRanges, value);
        }

        public bool TryGetMuscleRotationDegRange(EHumanoidValue value, out Vector2 range)
            => MuscleRotationDegRanges.TryGetValue(value, out range);

        /// <summary>
        /// Returns the effective rotation range for a muscle channel. Explicit per-muscle overrides win;
        /// otherwise the runtime falls back to the shared semantic range property for that channel.
        /// </summary>
        public Vector2 GetResolvedMuscleRotationDegRange(EHumanoidValue value)
            => MuscleRotationDegRanges.TryGetValue(value, out Vector2 range)
                ? range
                : GetFallbackMuscleRotationDegRange(value);

        /// <summary>
        /// Returns the shared fallback rotation range for a muscle channel when no explicit per-muscle
        /// override exists. Left/right mirrored channels intentionally share the same property.
        /// </summary>
        public Vector2 GetFallbackMuscleRotationDegRange(EHumanoidValue value)
            => value switch
            {
                EHumanoidValue.LeftEyeDownUp => LeftEyeDownUpRange,
                EHumanoidValue.LeftEyeInOut => LeftEyeInOutRange,
                EHumanoidValue.RightEyeDownUp => RightEyeDownUpRange,
                EHumanoidValue.RightEyeInOut => RightEyeInOutRange,

                EHumanoidValue.SpineFrontBack => SpineFrontBackDegRange,
                EHumanoidValue.SpineLeftRight => SpineLeftRightDegRange,
                EHumanoidValue.SpineTwistLeftRight => SpineTwistLeftRightDegRange,
                EHumanoidValue.ChestFrontBack => ChestFrontBackDegRange,
                EHumanoidValue.ChestLeftRight => ChestLeftRightDegRange,
                EHumanoidValue.ChestTwistLeftRight => ChestTwistLeftRightDegRange,
                EHumanoidValue.UpperChestFrontBack => UpperChestFrontBackDegRange,
                EHumanoidValue.UpperChestLeftRight => UpperChestLeftRightDegRange,
                EHumanoidValue.UpperChestTwistLeftRight => UpperChestTwistLeftRightDegRange,
                EHumanoidValue.NeckNodDownUp => NeckNodDownUpDegRange,
                EHumanoidValue.NeckTiltLeftRight => NeckTiltLeftRightDegRange,
                EHumanoidValue.NeckTurnLeftRight => NeckTurnLeftRightDegRange,
                EHumanoidValue.HeadNodDownUp => HeadNodDownUpDegRange,
                EHumanoidValue.HeadTiltLeftRight => HeadTiltLeftRightDegRange,
                EHumanoidValue.HeadTurnLeftRight => HeadTurnLeftRightDegRange,
                EHumanoidValue.JawClose => JawCloseDegRange,
                EHumanoidValue.JawLeftRight => JawLeftRightDegRange,

                EHumanoidValue.LeftShoulderDownUp or EHumanoidValue.RightShoulderDownUp => ShoulderDownUpDegRange,
                EHumanoidValue.LeftShoulderFrontBack or EHumanoidValue.RightShoulderFrontBack => ShoulderFrontBackDegRange,
                EHumanoidValue.LeftArmDownUp or EHumanoidValue.RightArmDownUp => ArmDownUpDegRange,
                EHumanoidValue.LeftArmFrontBack or EHumanoidValue.RightArmFrontBack => ArmFrontBackDegRange,
                EHumanoidValue.LeftArmTwistInOut or EHumanoidValue.RightArmTwistInOut => ArmTwistDegRange,
                EHumanoidValue.LeftForearmStretch or EHumanoidValue.RightForearmStretch => ForearmStretchDegRange,
                EHumanoidValue.LeftForearmTwistInOut or EHumanoidValue.RightForearmTwistInOut => ForearmTwistDegRange,
                EHumanoidValue.LeftHandDownUp or EHumanoidValue.RightHandDownUp => HandDownUpDegRange,
                EHumanoidValue.LeftHandInOut or EHumanoidValue.RightHandInOut => HandInOutDegRange,
                EHumanoidValue.LeftUpperLegFrontBack or EHumanoidValue.RightUpperLegFrontBack => UpperLegFrontBackDegRange,
                EHumanoidValue.LeftUpperLegInOut or EHumanoidValue.RightUpperLegInOut => UpperLegInOutDegRange,
                EHumanoidValue.LeftUpperLegTwistInOut or EHumanoidValue.RightUpperLegTwistInOut => UpperLegTwistDegRange,
                EHumanoidValue.LeftLowerLegStretch or EHumanoidValue.RightLowerLegStretch => LowerLegStretchDegRange,
                EHumanoidValue.LeftLowerLegTwistInOut or EHumanoidValue.RightLowerLegTwistInOut => LowerLegTwistDegRange,
                EHumanoidValue.LeftFootUpDown or EHumanoidValue.RightFootUpDown => FootUpDownDegRange,
                EHumanoidValue.LeftFootTwistInOut or EHumanoidValue.RightFootTwistInOut => FootTwistDegRange,
                EHumanoidValue.LeftToesUpDown or EHumanoidValue.RightToesUpDown => ToesUpDownDegRange,

                EHumanoidValue.LeftHandIndexSpread or EHumanoidValue.RightHandIndexSpread => IndexSpreadDegRange,
                EHumanoidValue.LeftHandIndex1Stretched or EHumanoidValue.RightHandIndex1Stretched => Index1StretchedDegRange,
                EHumanoidValue.LeftHandIndex2Stretched or EHumanoidValue.RightHandIndex2Stretched => Index2StretchedDegRange,
                EHumanoidValue.LeftHandIndex3Stretched or EHumanoidValue.RightHandIndex3Stretched => Index3StretchedDegRange,
                EHumanoidValue.LeftHandMiddleSpread or EHumanoidValue.RightHandMiddleSpread => MiddleSpreadDegRange,
                EHumanoidValue.LeftHandMiddle1Stretched or EHumanoidValue.RightHandMiddle1Stretched => Middle1StretchedDegRange,
                EHumanoidValue.LeftHandMiddle2Stretched or EHumanoidValue.RightHandMiddle2Stretched => Middle2StretchedDegRange,
                EHumanoidValue.LeftHandMiddle3Stretched or EHumanoidValue.RightHandMiddle3Stretched => Middle3StretchedDegRange,
                EHumanoidValue.LeftHandRingSpread or EHumanoidValue.RightHandRingSpread => RingSpreadDegRange,
                EHumanoidValue.LeftHandRing1Stretched or EHumanoidValue.RightHandRing1Stretched => Ring1StretchedDegRange,
                EHumanoidValue.LeftHandRing2Stretched or EHumanoidValue.RightHandRing2Stretched => Ring2StretchedDegRange,
                EHumanoidValue.LeftHandRing3Stretched or EHumanoidValue.RightHandRing3Stretched => Ring3StretchedDegRange,
                EHumanoidValue.LeftHandLittleSpread or EHumanoidValue.RightHandLittleSpread => LittleSpreadDegRange,
                EHumanoidValue.LeftHandLittle1Stretched or EHumanoidValue.RightHandLittle1Stretched => Little1StretchedDegRange,
                EHumanoidValue.LeftHandLittle2Stretched or EHumanoidValue.RightHandLittle2Stretched => Little2StretchedDegRange,
                EHumanoidValue.LeftHandLittle3Stretched or EHumanoidValue.RightHandLittle3Stretched => Little3StretchedDegRange,
                EHumanoidValue.LeftHandThumbSpread or EHumanoidValue.RightHandThumbSpread => ThumbSpreadDegRange,
                EHumanoidValue.LeftHandThumb1Stretched or EHumanoidValue.RightHandThumb1Stretched => Thumb1StretchedDegRange,
                EHumanoidValue.LeftHandThumb2Stretched or EHumanoidValue.RightHandThumb2Stretched => Thumb2StretchedDegRange,
                EHumanoidValue.LeftHandThumb3Stretched or EHumanoidValue.RightHandThumb3Stretched => Thumb3StretchedDegRange,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported humanoid muscle channel."),
            };

        public void SetMuscleRotationDegRange(EHumanoidValue value, Vector2 range)
        {
            if (!MuscleRotationDegRanges.TryAdd(value, range))
                MuscleRotationDegRanges[value] = range;
        }

        private Dictionary<EHumanoidValue, Vector2> _muscleScaleRanges = [];
        /// <summary>
        /// Optional per-channel scale ranges (min, max). Keyed by humanoid muscle enum.
        /// Intended for channels like Forearm Stretch.
        /// If a key is missing, the runtime falls back to built-in defaults.
        /// </summary>
        public Dictionary<EHumanoidValue, Vector2> MuscleScaleRanges
        {
            get => _muscleScaleRanges;
            set => SetField(ref _muscleScaleRanges, value);
        }

        public bool TryGetMuscleScaleRange(EHumanoidValue value, out Vector2 range)
            => MuscleScaleRanges.TryGetValue(value, out range);

        public void SetMuscleScaleRange(EHumanoidValue value, Vector2 range)
        {
            if (!MuscleScaleRanges.TryAdd(value, range))
                MuscleScaleRanges[value] = range;
        }

        // Rotation ranges are in degrees (min, max). These get converted to radians by HumanoidComponent.
        // ── Spine / Chest / Upper Chest ─────────────────────────────
        // Unity default per-muscle range: all ±40°.
        private Vector2 _spineFrontBackDegRange = new(-40.0f, 40.0f);
        public Vector2 SpineFrontBackDegRange
        {
            get => _spineFrontBackDegRange;
            set => SetField(ref _spineFrontBackDegRange, value);
        }

        private Vector2 _spineLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 SpineLeftRightDegRange
        {
            get => _spineLeftRightDegRange;
            set => SetField(ref _spineLeftRightDegRange, value);
        }

        private Vector2 _spineTwistLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 SpineTwistLeftRightDegRange
        {
            get => _spineTwistLeftRightDegRange;
            set => SetField(ref _spineTwistLeftRightDegRange, value);
        }

        private Vector2 _chestFrontBackDegRange = new(-40.0f, 40.0f);
        public Vector2 ChestFrontBackDegRange
        {
            get => _chestFrontBackDegRange;
            set => SetField(ref _chestFrontBackDegRange, value);
        }

        private Vector2 _chestLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 ChestLeftRightDegRange
        {
            get => _chestLeftRightDegRange;
            set => SetField(ref _chestLeftRightDegRange, value);
        }

        private Vector2 _chestTwistLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 ChestTwistLeftRightDegRange
        {
            get => _chestTwistLeftRightDegRange;
            set => SetField(ref _chestTwistLeftRightDegRange, value);
        }

        // Upper Chest is optional; when present, uses same range as Chest.
        private Vector2 _upperChestFrontBackDegRange = new(-40.0f, 40.0f);
        public Vector2 UpperChestFrontBackDegRange
        {
            get => _upperChestFrontBackDegRange;
            set => SetField(ref _upperChestFrontBackDegRange, value);
        }

        private Vector2 _upperChestLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 UpperChestLeftRightDegRange
        {
            get => _upperChestLeftRightDegRange;
            set => SetField(ref _upperChestLeftRightDegRange, value);
        }

        private Vector2 _upperChestTwistLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 UpperChestTwistLeftRightDegRange
        {
            get => _upperChestTwistLeftRightDegRange;
            set => SetField(ref _upperChestTwistLeftRightDegRange, value);
        }

        // ── Head / Neck ─────────────────────────────────────────────────
        // Unity default: all ±40°.
        private Vector2 _neckNodDownUpDegRange = new(-40.0f, 40.0f);
        public Vector2 NeckNodDownUpDegRange
        {
            get => _neckNodDownUpDegRange;
            set => SetField(ref _neckNodDownUpDegRange, value);
        }

        private Vector2 _neckTurnLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 NeckTurnLeftRightDegRange
        {
            get => _neckTurnLeftRightDegRange;
            set => SetField(ref _neckTurnLeftRightDegRange, value);
        }

        private Vector2 _neckTiltLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 NeckTiltLeftRightDegRange
        {
            get => _neckTiltLeftRightDegRange;
            set => SetField(ref _neckTiltLeftRightDegRange, value);
        }

        private Vector2 _headNodDownUpDegRange = new(-40.0f, 40.0f);
        public Vector2 HeadNodDownUpDegRange
        {
            get => _headNodDownUpDegRange;
            set => SetField(ref _headNodDownUpDegRange, value);
        }

        private Vector2 _headTurnLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 HeadTurnLeftRightDegRange
        {
            get => _headTurnLeftRightDegRange;
            set => SetField(ref _headTurnLeftRightDegRange, value);
        }

        private Vector2 _headTiltLeftRightDegRange = new(-40.0f, 40.0f);
        public Vector2 HeadTiltLeftRightDegRange
        {
            get => _headTiltLeftRightDegRange;
            set => SetField(ref _headTiltLeftRightDegRange, value);
        }

        // ── Jaw ─────────────────────────────────────────────────────────
        private Vector2 _jawLeftRightDegRange = new(-15.0f, 15.0f);
        public Vector2 JawLeftRightDegRange
        {
            get => _jawLeftRightDegRange;
            set => SetField(ref _jawLeftRightDegRange, value);
        }

        private Vector2 _jawCloseDegRange = new(-5.0f, 25.0f);
        public Vector2 JawCloseDegRange
        {
            get => _jawCloseDegRange;
            set => SetField(ref _jawCloseDegRange, value);
        }

        // ── Shoulder ────────────────────────────────────────────────────
        private Vector2 _shoulderDownUpDegRange = new(-15.0f, 30.0f);
        public Vector2 ShoulderDownUpDegRange
        {
            get => _shoulderDownUpDegRange;
            set => SetField(ref _shoulderDownUpDegRange, value);
        }

        private Vector2 _shoulderFrontBackDegRange = new(-15.0f, 15.0f);
        public Vector2 ShoulderFrontBackDegRange
        {
            get => _shoulderFrontBackDegRange;
            set => SetField(ref _shoulderFrontBackDegRange, value);
        }

        // ── Upper Arm ───────────────────────────────────────────────────
        private Vector2 _armTwistDegRange = new(-90.0f, 90.0f);
        public Vector2 ArmTwistDegRange
        {
            get => _armTwistDegRange;
            set => SetField(ref _armTwistDegRange, value);
        }

        private Vector2 _armDownUpDegRange = new(-60.0f, 100.0f);
        public Vector2 ArmDownUpDegRange
        {
            get => _armDownUpDegRange;
            set => SetField(ref _armDownUpDegRange, value);
        }

        private Vector2 _armFrontBackDegRange = new(-100.0f, 100.0f);
        public Vector2 ArmFrontBackDegRange
        {
            get => _armFrontBackDegRange;
            set => SetField(ref _armFrontBackDegRange, value);
        }

        // ── Forearm / Elbow ─────────────────────────────────────────────
        private Vector2 _forearmTwistDegRange = new(-90.0f, 90.0f);
        public Vector2 ForearmTwistDegRange
        {
            get => _forearmTwistDegRange;
            set => SetField(ref _forearmTwistDegRange, value);
        }

        private Vector2 _forearmStretchDegRange = new(-10.0f, 70.0f);
        public Vector2 ForearmStretchDegRange
        {
            get => _forearmStretchDegRange;
            set => SetField(ref _forearmStretchDegRange, value);
        }

        // ── Hand / Wrist ────────────────────────────────────────────────
        private Vector2 _handDownUpDegRange = new(-80.0f, 80.0f);
        public Vector2 HandDownUpDegRange
        {
            get => _handDownUpDegRange;
            set => SetField(ref _handDownUpDegRange, value);
        }

        private Vector2 _handInOutDegRange = new(-40.0f, 40.0f);
        public Vector2 HandInOutDegRange
        {
            get => _handInOutDegRange;
            set => SetField(ref _handInOutDegRange, value);
        }

        // ── Upper Leg ───────────────────────────────────────────────────
        private Vector2 _upperLegTwistDegRange = new(-60.0f, 60.0f);
        public Vector2 UpperLegTwistDegRange
        {
            get => _upperLegTwistDegRange;
            set => SetField(ref _upperLegTwistDegRange, value);
        }

        private Vector2 _upperLegFrontBackDegRange = new(-90.0f, 50.0f);
        public Vector2 UpperLegFrontBackDegRange
        {
            get => _upperLegFrontBackDegRange;
            set => SetField(ref _upperLegFrontBackDegRange, value);
        }

        private Vector2 _upperLegInOutDegRange = new(-60.0f, 60.0f);
        public Vector2 UpperLegInOutDegRange
        {
            get => _upperLegInOutDegRange;
            set => SetField(ref _upperLegInOutDegRange, value);
        }

        // ── Lower Leg / Knee ────────────────────────────────────────────
        private Vector2 _lowerLegTwistDegRange = new(-90.0f, 90.0f);
        public Vector2 LowerLegTwistDegRange
        {
            get => _lowerLegTwistDegRange;
            set => SetField(ref _lowerLegTwistDegRange, value);
        }

        private Vector2 _lowerLegStretchDegRange = new(-10.0f, 100.0f);
        public Vector2 LowerLegStretchDegRange
        {
            get => _lowerLegStretchDegRange;
            set => SetField(ref _lowerLegStretchDegRange, value);
        }

        // ── Foot / Toes ─────────────────────────────────────────────────
        private Vector2 _footTwistDegRange = new(-30.0f, 30.0f);
        public Vector2 FootTwistDegRange
        {
            get => _footTwistDegRange;
            set => SetField(ref _footTwistDegRange, value);
        }

        private Vector2 _footUpDownDegRange = new(-50.0f, 50.0f);
        public Vector2 FootUpDownDegRange
        {
            get => _footUpDownDegRange;
            set => SetField(ref _footUpDownDegRange, value);
        }

        private Vector2 _toesUpDownDegRange = new(-50.0f, 50.0f);
        public Vector2 ToesUpDownDegRange
        {
            get => _toesUpDownDegRange;
            set => SetField(ref _toesUpDownDegRange, value);
        }

        // ── Fingers ─────────────────────────────────────────────────────
        private Vector2 _thumbSpreadDegRange = new(-25.0f, 25.0f);
        public Vector2 ThumbSpreadDegRange { get => _thumbSpreadDegRange; set => SetField(ref _thumbSpreadDegRange, value); }
        private Vector2 _thumb1StretchedDegRange = new(-20.0f, 20.0f);
        public Vector2 Thumb1StretchedDegRange { get => _thumb1StretchedDegRange; set => SetField(ref _thumb1StretchedDegRange, value); }
        private Vector2 _thumb2StretchedDegRange = new(-40.0f, 35.0f);
        public Vector2 Thumb2StretchedDegRange { get => _thumb2StretchedDegRange; set => SetField(ref _thumb2StretchedDegRange, value); }
        private Vector2 _thumb3StretchedDegRange = new(-40.0f, 35.0f);
        public Vector2 Thumb3StretchedDegRange { get => _thumb3StretchedDegRange; set => SetField(ref _thumb3StretchedDegRange, value); }

        private Vector2 _indexSpreadDegRange = new(-20.0f, 20.0f);
        public Vector2 IndexSpreadDegRange { get => _indexSpreadDegRange; set => SetField(ref _indexSpreadDegRange, value); }
        private Vector2 _index1StretchedDegRange = new(-50.0f, 50.0f);
        public Vector2 Index1StretchedDegRange { get => _index1StretchedDegRange; set => SetField(ref _index1StretchedDegRange, value); }
        private Vector2 _index2StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Index2StretchedDegRange { get => _index2StretchedDegRange; set => SetField(ref _index2StretchedDegRange, value); }
        private Vector2 _index3StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Index3StretchedDegRange { get => _index3StretchedDegRange; set => SetField(ref _index3StretchedDegRange, value); }

        private Vector2 _middleSpreadDegRange = new(-7.5f, 7.5f);
        public Vector2 MiddleSpreadDegRange { get => _middleSpreadDegRange; set => SetField(ref _middleSpreadDegRange, value); }
        private Vector2 _middle1StretchedDegRange = new(-50.0f, 50.0f);
        public Vector2 Middle1StretchedDegRange { get => _middle1StretchedDegRange; set => SetField(ref _middle1StretchedDegRange, value); }
        private Vector2 _middle2StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Middle2StretchedDegRange { get => _middle2StretchedDegRange; set => SetField(ref _middle2StretchedDegRange, value); }
        private Vector2 _middle3StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Middle3StretchedDegRange { get => _middle3StretchedDegRange; set => SetField(ref _middle3StretchedDegRange, value); }

        private Vector2 _ringSpreadDegRange = new(-7.5f, 7.5f);
        public Vector2 RingSpreadDegRange { get => _ringSpreadDegRange; set => SetField(ref _ringSpreadDegRange, value); }
        private Vector2 _ring1StretchedDegRange = new(-50.0f, 50.0f);
        public Vector2 Ring1StretchedDegRange { get => _ring1StretchedDegRange; set => SetField(ref _ring1StretchedDegRange, value); }
        private Vector2 _ring2StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Ring2StretchedDegRange { get => _ring2StretchedDegRange; set => SetField(ref _ring2StretchedDegRange, value); }
        private Vector2 _ring3StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Ring3StretchedDegRange { get => _ring3StretchedDegRange; set => SetField(ref _ring3StretchedDegRange, value); }

        private Vector2 _littleSpreadDegRange = new(-20.0f, 20.0f);
        public Vector2 LittleSpreadDegRange { get => _littleSpreadDegRange; set => SetField(ref _littleSpreadDegRange, value); }
        private Vector2 _little1StretchedDegRange = new(-50.0f, 50.0f);
        public Vector2 Little1StretchedDegRange { get => _little1StretchedDegRange; set => SetField(ref _little1StretchedDegRange, value); }
        private Vector2 _little2StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Little2StretchedDegRange { get => _little2StretchedDegRange; set => SetField(ref _little2StretchedDegRange, value); }
        private Vector2 _little3StretchedDegRange = new(-45.0f, 45.0f);
        public Vector2 Little3StretchedDegRange { get => _little3StretchedDegRange; set => SetField(ref _little3StretchedDegRange, value); }

        /// <summary>
        /// Negates all min/max range values (swaps sign on both X and Y components).
        /// Useful when a skeleton's local axes are mirrored relative to the expected convention.
        /// </summary>
        public void NegateAllRanges()
        {
            SpineFrontBackDegRange = NegateRange(SpineFrontBackDegRange);
            SpineLeftRightDegRange = NegateRange(SpineLeftRightDegRange);
            SpineTwistLeftRightDegRange = NegateRange(SpineTwistLeftRightDegRange);
            ChestFrontBackDegRange = NegateRange(ChestFrontBackDegRange);
            ChestLeftRightDegRange = NegateRange(ChestLeftRightDegRange);
            ChestTwistLeftRightDegRange = NegateRange(ChestTwistLeftRightDegRange);
            UpperChestFrontBackDegRange = NegateRange(UpperChestFrontBackDegRange);
            UpperChestLeftRightDegRange = NegateRange(UpperChestLeftRightDegRange);
            UpperChestTwistLeftRightDegRange = NegateRange(UpperChestTwistLeftRightDegRange);
            NeckNodDownUpDegRange = NegateRange(NeckNodDownUpDegRange);
            NeckTurnLeftRightDegRange = NegateRange(NeckTurnLeftRightDegRange);
            NeckTiltLeftRightDegRange = NegateRange(NeckTiltLeftRightDegRange);
            HeadNodDownUpDegRange = NegateRange(HeadNodDownUpDegRange);
            HeadTurnLeftRightDegRange = NegateRange(HeadTurnLeftRightDegRange);
            HeadTiltLeftRightDegRange = NegateRange(HeadTiltLeftRightDegRange);
            JawLeftRightDegRange = NegateRange(JawLeftRightDegRange);
            JawCloseDegRange = NegateRange(JawCloseDegRange);
            ShoulderDownUpDegRange = NegateRange(ShoulderDownUpDegRange);
            ShoulderFrontBackDegRange = NegateRange(ShoulderFrontBackDegRange);
            ArmTwistDegRange = NegateRange(ArmTwistDegRange);
            ArmDownUpDegRange = NegateRange(ArmDownUpDegRange);
            ArmFrontBackDegRange = NegateRange(ArmFrontBackDegRange);
            ForearmTwistDegRange = NegateRange(ForearmTwistDegRange);
            ForearmStretchDegRange = NegateRange(ForearmStretchDegRange);
            HandDownUpDegRange = NegateRange(HandDownUpDegRange);
            HandInOutDegRange = NegateRange(HandInOutDegRange);
            UpperLegTwistDegRange = NegateRange(UpperLegTwistDegRange);
            UpperLegFrontBackDegRange = NegateRange(UpperLegFrontBackDegRange);
            UpperLegInOutDegRange = NegateRange(UpperLegInOutDegRange);
            LowerLegTwistDegRange = NegateRange(LowerLegTwistDegRange);
            LowerLegStretchDegRange = NegateRange(LowerLegStretchDegRange);
            FootTwistDegRange = NegateRange(FootTwistDegRange);
            FootUpDownDegRange = NegateRange(FootUpDownDegRange);
            ToesUpDownDegRange = NegateRange(ToesUpDownDegRange);
            ThumbSpreadDegRange = NegateRange(ThumbSpreadDegRange);
            Thumb1StretchedDegRange = NegateRange(Thumb1StretchedDegRange);
            Thumb2StretchedDegRange = NegateRange(Thumb2StretchedDegRange);
            Thumb3StretchedDegRange = NegateRange(Thumb3StretchedDegRange);
            IndexSpreadDegRange = NegateRange(IndexSpreadDegRange);
            Index1StretchedDegRange = NegateRange(Index1StretchedDegRange);
            Index2StretchedDegRange = NegateRange(Index2StretchedDegRange);
            Index3StretchedDegRange = NegateRange(Index3StretchedDegRange);
            MiddleSpreadDegRange = NegateRange(MiddleSpreadDegRange);
            Middle1StretchedDegRange = NegateRange(Middle1StretchedDegRange);
            Middle2StretchedDegRange = NegateRange(Middle2StretchedDegRange);
            Middle3StretchedDegRange = NegateRange(Middle3StretchedDegRange);
            RingSpreadDegRange = NegateRange(RingSpreadDegRange);
            Ring1StretchedDegRange = NegateRange(Ring1StretchedDegRange);
            Ring2StretchedDegRange = NegateRange(Ring2StretchedDegRange);
            Ring3StretchedDegRange = NegateRange(Ring3StretchedDegRange);
            LittleSpreadDegRange = NegateRange(LittleSpreadDegRange);
            Little1StretchedDegRange = NegateRange(Little1StretchedDegRange);
            Little2StretchedDegRange = NegateRange(Little2StretchedDegRange);
            Little3StretchedDegRange = NegateRange(Little3StretchedDegRange);

            if (MuscleRotationDegRanges.Count > 0)
            {
                Dictionary<EHumanoidValue, Vector2> negatedOverrides = new(MuscleRotationDegRanges.Count);
                foreach (var pair in MuscleRotationDegRanges)
                    negatedOverrides[pair.Key] = NegateRange(pair.Value);
                MuscleRotationDegRanges = negatedOverrides;
            }

            if (MuscleScaleRanges.Count > 0)
            {
                Dictionary<EHumanoidValue, Vector2> negatedScaleOverrides = new(MuscleScaleRanges.Count);
                foreach (var pair in MuscleScaleRanges)
                    negatedScaleOverrides[pair.Key] = NegateRange(pair.Value);
                MuscleScaleRanges = negatedScaleOverrides;
            }
        }

        private static Vector2 NegateRange(Vector2 range)
            => new(-range.Y, -range.X);

        // Per-bone axis mapping for non-standard skeletons.
        // Key: bone name, Values: which local axis maps to twist (yaw), front-back (pitch), left-right (roll).
        // Default: Y = twist, X = pitch, Z = roll (standard humanoid convention).
        private Dictionary<string, BoneAxisMapping> _boneAxisMappings = [];
        /// <summary>
        /// Optional per-bone axis remapping for models with non-standard local-axis conventions.
        /// When empty, the default humanoid convention (Y=twist, X=front-back, Z=left-right) is assumed.
        /// </summary>
        public Dictionary<string, BoneAxisMapping> BoneAxisMappings
        {
            get => _boneAxisMappings;
            set => SetField(ref _boneAxisMappings, value);
        }

        public bool TryGetBoneAxisMapping(string boneName, out BoneAxisMapping mapping)
            => BoneAxisMappings.TryGetValue(boneName, out mapping);

        // ── IK goal policy ──────────────────────────────────────────────
        private EHumanoidIKGoalPolicy _ikGoalPolicy = EHumanoidIKGoalPolicy.ApplyIfCalibrated;
        /// <summary>
        /// Controls how animation-driven IK goal channels (LeftFootT/Q, RightHandT/Q, etc.)
        /// are handled at runtime. When <see cref="EHumanoidIKGoalPolicy.ApplyIfCalibrated"/>,
        /// goals are only applied if <see cref="IsIKCalibrated"/> is true.
        /// </summary>
        public EHumanoidIKGoalPolicy IKGoalPolicy
        {
            get => _ikGoalPolicy;
            set => SetField(ref _ikGoalPolicy, value);
        }

        private bool _isIKCalibrated;
        /// <summary>
        /// Whether the avatar has proper IK calibration data (avatar-space → world-space conversion).
        /// When false and <see cref="IKGoalPolicy"/> is <see cref="EHumanoidIKGoalPolicy.ApplyIfCalibrated"/>,
        /// animation-driven IK goals are silently skipped.
        /// Set to true by the AvatarHumanoidProfileBuilder when calibration succeeds.
        /// </summary>
        public bool IsIKCalibrated
        {
            get => _isIKCalibrated;
            set => SetField(ref _isIKCalibrated, value);
        }

        // ── Profile confidence ──────────────────────────────────────────
        private float _profileConfidence;
        /// <summary>
        /// Overall calibration confidence in [0, 1] from the last profile build.
        /// Set by <see cref="AvatarHumanoidProfileBuilder.BuildProfile"/>.
        /// Values below 0.6 indicate low confidence and trigger conservative fallback
        /// behavior (e.g. IK goals disabled).
        /// </summary>
        public float ProfileConfidence
        {
            get => _profileConfidence;
            set => SetField(ref _profileConfidence, value);
        }

        private string? _profileSource;
        /// <summary>
        /// Describes how the current profile was obtained: "auto-generated", "cached", or "manual".
        /// </summary>
        public string? ProfileSource
        {
            get => _profileSource;
            set => SetField(ref _profileSource, value);
        }
    }

    /// <summary>
    /// Controls how animation-driven IK goal channels are applied at runtime.
    /// </summary>
    public enum EHumanoidIKGoalPolicy
    {
        /// <summary>
        /// IK goal channels from animation clips are ignored entirely.
        /// </summary>
        Ignore = 0,

        /// <summary>
        /// IK goal channels are applied only when the avatar has valid calibration data.
        /// This is the safe default: prevents broken IK when avatar-space conversion is unavailable.
        /// </summary>
        ApplyIfCalibrated,

        /// <summary>
        /// IK goal channels are always applied regardless of calibration state.
        /// Use only when you know the IK targets are in the correct space.
        /// </summary>
        AlwaysApply,
    }

    /// <summary>
    /// Defines how a bone's local axes map to humanoid twist/front-back/left-right rotation axes.
    /// </summary>
    public struct BoneAxisMapping
    {
        /// <summary>
        /// Which Euler component to use for twist (yaw). 0=X, 1=Y, 2=Z.
        /// </summary>
        public int TwistAxis { get; set; }

        /// <summary>
        /// Sign for twist axis contribution. Use +1 or -1.
        /// </summary>
        public int TwistSign { get; set; }

        /// <summary>
        /// Which Euler component to use for front-back (pitch). 0=X, 1=Y, 2=Z.
        /// </summary>
        public int FrontBackAxis { get; set; }

        /// <summary>
        /// Sign for front-back axis contribution. Use +1 or -1.
        /// </summary>
        public int FrontBackSign { get; set; }

        /// <summary>
        /// Which Euler component to use for left-right (roll). 0=X, 1=Y, 2=Z.
        /// </summary>
        public int LeftRightAxis { get; set; }

        /// <summary>
        /// Sign for left-right axis contribution. Use +1 or -1.
        /// </summary>
        public int LeftRightSign { get; set; }

        /// <summary>
        /// Default mapping: Yaw=Y(1), Pitch=X(0), Roll=Z(2).
        /// </summary>
        public static BoneAxisMapping Default => new()
        {
            TwistAxis = 1,
            TwistSign = 1,
            FrontBackAxis = 0,
            FrontBackSign = 1,
            LeftRightAxis = 2,
            LeftRightSign = 1,
        };
    }
}
