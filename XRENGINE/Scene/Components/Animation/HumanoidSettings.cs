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
    }
}
