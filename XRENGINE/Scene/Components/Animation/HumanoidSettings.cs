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

        private Vector2 _leftEyeDownUpRange = new(-30.0f, 30.0f);
        public Vector2 LeftEyeDownUpRange
        {
            get => _leftEyeDownUpRange;
            set => SetField(ref _leftEyeDownUpRange, value);
        }

        private Vector2 _leftEyeInOutRange = new(-30.0f, 30.0f);
        public Vector2 LeftEyeInOutRange
        {
            get => _leftEyeInOutRange;
            set => SetField(ref _leftEyeInOutRange, value);
        }

        private Vector2 _rightEyeDownUpRange = new(-30.0f, 30.0f);
        public Vector2 RightEyeDownUpRange
        {
            get => _rightEyeDownUpRange;
            set => SetField(ref _rightEyeDownUpRange, value);
        }

        private Vector2 _rightEyeInOutRange = new(-30.0f, 30.0f);
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
        private Vector2 _spineFrontBackDegRange = new(-18.0f, 18.0f);
        public Vector2 SpineFrontBackDegRange
        {
            get => _spineFrontBackDegRange;
            set => SetField(ref _spineFrontBackDegRange, value);
        }

        private Vector2 _spineLeftRightDegRange = new(-12.0f, 12.0f);
        public Vector2 SpineLeftRightDegRange
        {
            get => _spineLeftRightDegRange;
            set => SetField(ref _spineLeftRightDegRange, value);
        }

        private Vector2 _spineTwistLeftRightDegRange = new(-15.0f, 15.0f);
        public Vector2 SpineTwistLeftRightDegRange
        {
            get => _spineTwistLeftRightDegRange;
            set => SetField(ref _spineTwistLeftRightDegRange, value);
        }

        private Vector2 _chestFrontBackDegRange = new(-14.0f, 14.0f);
        public Vector2 ChestFrontBackDegRange
        {
            get => _chestFrontBackDegRange;
            set => SetField(ref _chestFrontBackDegRange, value);
        }

        private Vector2 _chestLeftRightDegRange = new(-10.0f, 10.0f);
        public Vector2 ChestLeftRightDegRange
        {
            get => _chestLeftRightDegRange;
            set => SetField(ref _chestLeftRightDegRange, value);
        }

        private Vector2 _chestTwistLeftRightDegRange = new(-12.0f, 12.0f);
        public Vector2 ChestTwistLeftRightDegRange
        {
            get => _chestTwistLeftRightDegRange;
            set => SetField(ref _chestTwistLeftRightDegRange, value);
        }

        private Vector2 _upperChestFrontBackDegRange = new(-10.0f, 10.0f);
        public Vector2 UpperChestFrontBackDegRange
        {
            get => _upperChestFrontBackDegRange;
            set => SetField(ref _upperChestFrontBackDegRange, value);
        }

        private Vector2 _upperChestLeftRightDegRange = new(-7.0f, 7.0f);
        public Vector2 UpperChestLeftRightDegRange
        {
            get => _upperChestLeftRightDegRange;
            set => SetField(ref _upperChestLeftRightDegRange, value);
        }

        private Vector2 _upperChestTwistLeftRightDegRange = new(-8.0f, 8.0f);
        public Vector2 UpperChestTwistLeftRightDegRange
        {
            get => _upperChestTwistLeftRightDegRange;
            set => SetField(ref _upperChestTwistLeftRightDegRange, value);
        }

        private Vector2 _neckNodDownUpDegRange = new(-18.0f, 18.0f);
        public Vector2 NeckNodDownUpDegRange
        {
            get => _neckNodDownUpDegRange;
            set => SetField(ref _neckNodDownUpDegRange, value);
        }

        private Vector2 _headNodDownUpDegRange = new(-14.0f, 14.0f);
        public Vector2 HeadNodDownUpDegRange
        {
            get => _headNodDownUpDegRange;
            set => SetField(ref _headNodDownUpDegRange, value);
        }
    }
}
